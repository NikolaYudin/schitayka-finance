using System.Globalization;

namespace SchitayKa.Api.Services;

/// <summary>
/// Поля QR-кода чека ФНС «старого» формата: t, s, fn, i, fp, n.
/// Разбор полностью локальный — запросов к ФНС не выполняется.
/// t — сумма чека, s — сумма позиции, fn — фискальный накопитель,
/// i — номер фискального документа, fp — фискальный признак, n — номер смены.
/// </summary>
public sealed record ReceiptQrInfo(
    decimal Amount,
    decimal? PositionAmount,
    string Fn,
    string FiscalDocument,
    string FiscalSign,
    string? Shift)
{
    public const string Source = "QR ФНС";
}

/// <summary>Результат локального разбора QR-строки.</summary>
public sealed record QrParseResult(bool Ok, ReceiptQrInfo? Data, string? Error)
{
    public static QrParseResult Success(ReceiptQrInfo data) => new(true, data, null);
    public static QrParseResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Парсер QR-строки чека ФНС. Поддерживает как голую строку «t=…&s=…&fn=…»,
/// так и вставленную ссылку с параметрами после «?». Новый base64-формат
/// чеков (2020+) не разбирается: в нём нет полей t/s/fn/i/fp в открытом виде.
/// </summary>
public static class ReceiptQrParser
{
    public static QrParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return QrParseResult.Fail("Вставьте текст QR-кода чека.");

        var text = raw.Trim();
        var query = text.IndexOf('?');
        if (query >= 0) text = text[(query + 1)..];
        if (text.Length > 2000)
            return QrParseResult.Fail("QR-строка слишком длинная.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = part[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            try { value = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { /* оставляем значение как есть */ }
            fields[part[..eq].Trim()] = value;
        }

        if (fields.Count == 0)
            return QrParseResult.Fail("QR-строка не содержит полей t, s, fn, i, fp.");

        var positionAmount = ParseMoney(fields.GetValueOrDefault("s"));
        var amount = ParseMoney(fields.GetValueOrDefault("t")) ?? positionAmount;
        if (amount is null)
            return QrParseResult.Fail("В QR не найдена сумма (поля t или s).");

        var fn = fields.GetValueOrDefault("fn") ?? "";
        var document = fields.GetValueOrDefault("i") ?? "";
        var sign = fields.GetValueOrDefault("fp") ?? "";
        if (!IsDigits(fn, 10, 16))
            return QrParseResult.Fail("Неверный номер фискального накопителя (fn).");
        if (!IsDigits(document, 1, 10))
            return QrParseResult.Fail("Неверный номер фискального документа (i).");
        if (!IsDigits(sign, 1, 10))
            return QrParseResult.Fail("Неверный фискальный признак (fp).");

        var shift = fields.GetValueOrDefault("n");
        if (shift is not null && !IsDigits(shift, 1, 6)) shift = null; // поле необязательное

        return QrParseResult.Success(new ReceiptQrInfo(
            amount.Value, positionAmount, fn, document, sign, shift));
    }

    private static bool IsDigits(string? value, int min, int max) =>
        !string.IsNullOrEmpty(value)
        && value.Length >= min && value.Length <= max
        && value.All(char.IsDigit);

    private static decimal? ParseMoney(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim()
            .Replace(" ", "").Replace("\u00A0", "")
            .Replace("₽", "").Replace("руб.", "").Replace("руб", "")
            .Replace("RUB", "", StringComparison.OrdinalIgnoreCase);
        var comma = s.LastIndexOf(',');
        var dot = s.LastIndexOf('.');
        if (comma > dot)
            s = s[..comma].Replace(".", "") + "." + s[(comma + 1)..];
        else if (dot > comma)
            s = s[..dot].Replace(",", "") + "." + s[(dot + 1)..];

        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return value is > 0 and <= 10_000_000 ? Math.Round(value, 2) : null;
    }
}
