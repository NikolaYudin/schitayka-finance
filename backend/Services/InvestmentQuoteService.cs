using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

public sealed record QuoteRefreshResult(
    int Updated,
    int Failed,
    int Skipped,
    int Quoted,
    DateTime? LastSuccessAt,
    string? LastError);

/// <summary>
/// Обновление текущих цен позиций по тикерам через <see cref="IQuoteProvider"/>.
/// Не блокирует интерфейс: при недоступности источника старая (ручная) цена остаётся,
/// а в карточке показывается текст ошибки. Свежесть цены контролируется полем
/// <see cref="InvestmentHolding.PriceUpdatedAt"/> (по умолчанию 5 минут).
/// </summary>
public sealed class InvestmentQuoteService
{
    /// <summary>Как часто разрешено обновлять одну и ту же котировку.</summary>
    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IQuoteProvider _provider;
    private readonly ILogger<InvestmentQuoteService> _log;

    public InvestmentQuoteService(
        AppDbContext db, IQuoteProvider provider, ILogger<InvestmentQuoteService> log)
    {
        _db = db;
        _provider = provider;
        _log = log;
    }

    /// <summary>Имя источника котировок (для отображения в UI).</summary>
    public string ProviderName => _provider.Name;

    /// <summary>
    /// Обновить цены позиций пользователя. <paramref name="force"/> игнорирует свежесть цен.
    /// </summary>
    public async Task<QuoteRefreshResult> RefreshAsync(int userId, bool force, CancellationToken ct = default)
    {
        if (userId <= 0)
            return new QuoteRefreshResult(0, 0, 0, 0, null, null);

        var holdings = await _db.InvestmentHoldings
            .Where(h => h.UserId == userId && h.AssetType != AssetType.Cash)
            .OrderBy(h => h.Id)
            .ToListAsync(ct);

        var quoted = holdings.Count(h => !string.IsNullOrWhiteSpace(h.Ticker));
        var updated = 0;
        var failed = 0;
        var skipped = 0;
        var now = DateTime.UtcNow;

        foreach (var h in holdings)
        {
            if (string.IsNullOrWhiteSpace(h.Ticker))
            {
                skipped++;
                continue;
            }

            if (!force && h.PriceUpdatedAt is not null
                && now - h.PriceUpdatedAt.Value < AutoRefreshInterval)
            {
                skipped++;
                continue;
            }

            var lookup = await _provider.GetAsync(h.Ticker!, force, ct);
            if (lookup.Outcome == QuoteOutcome.Found && lookup.Result is not null)
            {
                h.CurrentPrice = lookup.Result.Price;
                h.QuoteSource = QuoteSource.MoexIss;
                h.PriceUpdatedAt = now;
                h.QuoteError = null;
                h.UpdatedAt = now;
                updated++;
                continue;
            }

            if (lookup.Outcome == QuoteOutcome.Unavailable)
            {
                // Источник недоступен — остальные тикеры тоже не получить, прекращаем перебор.
                h.QuoteError = lookup.Error ?? "Источник временно недоступен.";
                h.UpdatedAt = now;
                failed++;
                break;
            }

            // Тикер не найден: ручная цена остаётся, ошибку показываем в карточке.
            h.QuoteError = lookup.Error ?? "Тикер не найден.";
            h.UpdatedAt = now;
            failed++;
        }

        if (updated > 0 || failed > 0)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Не удалось сохранить обновлённые котировки (UserId {UserId})", userId);
                return new QuoteRefreshResult(0, 0, skipped, quoted, null, "Не удалось сохранить котировки.");
            }
        }

        var (lastSuccess, lastError) = await StatusAsync(userId, ct);
        return new QuoteRefreshResult(updated, failed, skipped, quoted, lastSuccess, lastError);
    }

    /// <summary>Время последнего успешного обновления цены и первая активная ошибка.</summary>
    public async Task<(DateTime? LastSuccessAt, string? LastError)> StatusAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return (null, null);

        var lastSuccess = await _db.InvestmentHoldings
            .Where(h => h.UserId == userId && h.PriceUpdatedAt != null)
            .Select(h => h.PriceUpdatedAt)
            .OrderByDescending(x => x)
            .FirstOrDefaultAsync(ct);

        var lastError = await _db.InvestmentHoldings
            .Where(h => h.UserId == userId && h.AssetType != AssetType.Cash && h.QuoteError != null)
            .Select(h => h.QuoteError!)
            .FirstOrDefaultAsync(ct);

        return (lastSuccess, lastError);
    }
}
