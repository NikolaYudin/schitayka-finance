namespace SchitayKa.Api.Services;

/// <summary>Результат поиска котировки: нашли / тикер не найден / источник недоступен.</summary>
public enum QuoteOutcome
{
    Found = 0,
    NotFound = 1,
    Unavailable = 2,
}

/// <summary>Найденная котировка.</summary>
public sealed record QuoteResult(decimal Price, string Source, string? Name = null);

/// <summary>Результат запроса котировки у провайдера.</summary>
public sealed record QuoteLookup(QuoteOutcome Outcome, QuoteResult? Result, string? Error)
{
    public static QuoteLookup Found(QuoteResult result) => new(QuoteOutcome.Found, result, null);
    public static QuoteLookup NotFound(string error) => new(QuoteOutcome.NotFound, null, error);
    public static QuoteLookup Unavailable(string error) => new(QuoteOutcome.Unavailable, null, error);
}

/// <summary>
/// Источник стоимости по коду объекта (тикер/ISIN). Реализуется для MOEX ISS,
/// при недоступности цена остаётся ручной (пункт 1.0 плана).
/// </summary>
public interface IQuoteProvider
{
    /// <summary>Короткое имя источника для отображения в UI.</summary>
    string Name { get; }

    /// <summary>
    /// Получить последнюю цену по тикеру. <paramref name="force"/> — игнорировать кэш.
    /// </summary>
    Task<QuoteLookup> GetAsync(string ticker, bool force, CancellationToken ct = default);
}
