using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SchitayKa.Api.Services;

/// <summary>
/// Котировки MOEX ISS (публичный API Мосбиржи, без ключей).
/// Используются только публичные тикеры, никакие пользовательские данные не передаются.
/// Результат кэшируется на 5 минут, при сбое — backoff на 1 минуту, чтобы не спамить запросами.
/// </summary>
public sealed class MoexIssQuoteProvider : IQuoteProvider
{
    public const string SourceName = "MOEX ISS";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<MoexIssQuoteProvider> _log;
    private readonly ConcurrentDictionary<string, (QuoteLookup Lookup, DateTimeOffset At)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;

    public MoexIssQuoteProvider(IHttpClientFactory http, ILogger<MoexIssQuoteProvider> log)
    {
        _http = http;
        _log = log;
    }

    public string Name => SourceName;

    public async Task<QuoteLookup> GetAsync(string ticker, bool force, CancellationToken ct = default)
    {
        var key = Normalize(ticker);
        if (key.Length == 0)
            return QuoteLookup.NotFound("Тикер не указан.");

        if (!force && TryGetCached(key, out var cached))
            return cached;

        if (!force && DateTimeOffset.UtcNow - _lastFailureAt < FailureBackoff)
            return QuoteLookup.Unavailable("Источник временно недоступен — повторим позже.");

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && TryGetCached(key, out var cached2))
                return cached2;

            var lookup = await FetchAsync(key, ct);
            _cache[key] = (lookup, DateTimeOffset.UtcNow);
            if (lookup.Outcome == QuoteOutcome.Unavailable)
                _lastFailureAt = DateTimeOffset.UtcNow;
            return lookup;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetCached(string key, out QuoteLookup lookup)
    {
        lookup = default!;
        if (!_cache.TryGetValue(key, out var entry)) return false;
        if (DateTimeOffset.UtcNow - entry.At >= CacheTtl) return false;
        lookup = entry.Lookup;
        return true;
    }

    private async Task<QuoteLookup> FetchAsync(string ticker, CancellationToken ct)
    {
        // Сначала акции/ETF (рынок shares), затем облигации (bonds).
        foreach (var market in new[] { "shares", "bonds" })
        {
            var url = $"https://iss.moex.com/iss/engines/stock/markets/{market}"
                      + $"/securities/{Uri.EscapeDataString(ticker)}.json?iss.meta=off&iss.only=securities";
            try
            {
                var client = _http.CreateClient("moex");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("SchitayKa-InvestQuotes/1.0");
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;
                if (!response.IsSuccessStatusCode)
                    return QuoteLookup.Unavailable($"MOEX ISS вернул {(int)response.StatusCode}.");

                var json = await response.Content.ReadAsStringAsync(ct);
                if (TryParsePrice(json, out var price, out var name) && price > 0m)
                    return QuoteLookup.Found(new QuoteResult(price, SourceName, name));

                // 200 OK, но цены нет — пробуем следующий рынок.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                _log.LogWarning(ex, "MOEX ISS: не удалось получить котировку {Ticker}", ticker);
                return QuoteLookup.Unavailable("MOEX ISS временно недоступен.");
            }
        }

        return QuoteLookup.NotFound("Тикер не найден на MOEX ISS.");
    }

    /// <summary>Безопасный парсинг секции securities: берём первую строку с положительным PREVPRICE.</summary>
    public static bool TryParsePrice(string json, out decimal price, out string? name)
    {
        price = 0m;
        name = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("securities", out var sec) || sec.ValueKind != JsonValueKind.Object)
                return false;
            if (!sec.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
                return false;
            if (!sec.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return false;

            var names = new List<string>(cols.GetArrayLength());
            foreach (var c in cols.EnumerateArray())
                names.Add(c.GetString() ?? "");

            var iPrev = names.IndexOf("PREVPRICE");
            if (iPrev < 0) return false;
            var iName = names.IndexOf("SHORTNAME");

            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() <= iPrev)
                    continue;
                var el = row[iPrev];
                var p = el.ValueKind == JsonValueKind.Number ? el.GetDecimal() : 0m;
                if (p <= 0m) continue;

                price = p;
                if (iName >= 0 && row.GetArrayLength() > iName)
                    name = row[iName].GetString();
                return true;
            }
        }
        catch (JsonException)
        {
            // Некорректный ответ — считаем, что цены нет.
        }
        return false;
    }

    private static string Normalize(string ticker)
    {
        var t = ticker.Trim().ToUpperInvariant();
        // ISIN (например, RU0009029540) MOEX тоже понимает, но лишние символы убираем.
        return t.Replace(" ", "");
    }
}
