using System.Globalization;
using System.Text.RegularExpressions;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>Минимальная ссылка на категорию для подсказки при импорте.</summary>
public sealed record CategoryRef(int Id, string Name, TxType Type);

public sealed record ReceiptParseResult(
    decimal? Amount,
    string Date,
    int Type,
    string TypeName,
    int? CategoryId,
    string? CategoryName,
    string? Merchant,
    string Comment,
    double Confidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Lines,
    string RawText);

/// <summary>
/// Разбирает текст чека/банковского скриншота: сумму, дату, тип, продавца и категорию.
/// Эвристики намеренно консервативные: пользователь всегда видит результат и может
/// поправить поля перед сохранением операции.
/// </summary>
public static class ReceiptParser
{
    private static readonly Regex NumberRx = new(
        @"(?<![\d])(?:\d{1,3}(?:[ \u00A0]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?![\d])",
        RegexOptions.Compiled);

    private static readonly Regex IsoDateRx = new(
        @"\b(20\d{2})-(\d{1,2})-(\d{1,2})\b", RegexOptions.Compiled);

    private static readonly Regex DotDateRx = new(
        @"\b(\d{1,2})[./](\d{1,2})[./](\d{2,4})\b", RegexOptions.Compiled);

    private static readonly Regex LongDigitsRx = new(@"\d{10,}", RegexOptions.Compiled);

    private static readonly Regex IncomeRx = new(
        @"зачислен|поступлен|перевод\s+от|пополнен|возврат\s+средств|\bincome\b|\breceived\b|\bcredit\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SkipMerchantRx = new(
        @"^(чек|квитанц|итог|итого|сумма|ндс|кассир|смена|дата|время|тел|адрес|операц|карта|счет|счёт|баланс|терминал|банкомат)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LegalFormRx = new(
        @"(ООО|ОАО|ЗАО|ПАО|АО|ИП|МАГАЗИН|КАФЕ|РЕСТОРАН|АПТЕКА|АЗС|МАРКЕТ|SHOP|STORE)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string[]> CategoryHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Продукты"] =
        [
            "продукт", "пятёрочка", "пятерочка", "магнит", "лента", "перекрёсток", "перекресток",
            "вкусвилл", "ашан", "дикси", "супермаркет", "гипермаркет", "овощ", "мясо", "хлеб",
        ],
        ["Транспорт"] =
        [
            "такси", "бензин", "азс", "лукойл", "газпромнефть", "метро", "парковка", "автобус",
            "транспорт", "каршеринг", "электричк",
        ],
        ["ЖКХ"] =
        [
            "жкх", "коммунал", "электро", "водоснабж", "отоплен", "энергосбыт", "газоснабж",
        ],
        ["Здоровье"] =
        [
            "аптека", "клиника", "медиц", "стоматолог", "здоров", "поликлиник", "анализ",
        ],
        ["Развлечения"] =
        [
            "кафе", "ресторан", "кофе", "бар", "кино", "концерт", "театр", "развлечен", "боулинг",
        ],
        ["Одежда"] =
        [
            "одежда", "обувь", "zara", "h&m", "спортмастер", "трикотаж", "джинс",
        ],
        ["Связь"] =
        [
            "мтс", "билайн", "мегафон", "теле2", "связь", "интернет", "мобильн",
        ],
        ["Дом"] =
        [
            "мебель", "ремонт", "хозтовар", "leroy", "леруа", "ikea", "икеа", "посуда",
        ],
        ["Образование"] =
        [
            "школ", "курс", "обучен", "книг", "учебник", "университет",
        ],
        ["Подписки"] =
        [
            "подписк", "netflix", "spotify", "яндекс плюс", "apple", "google", "telegram premium",
        ],
    };

    private static readonly string[] StrongAmountKeywords =
    [
        "к оплате", "итого", "итог", "сумма", "total", "amount", "оплачено", "списано", "к списанию",
    ];

    private static readonly string[] RussianMonths =
    [
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря",
    ];

    public static ReceiptParseResult Parse(string text, IReadOnlyList<CategoryRef> categories)
    {
        var normalized = (text ?? "").Replace('\u00A0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n')
            .Select(NormalizeLine)
            .Where(l => l.Length > 0)
            .ToList();

        var dateFound = TryExtractDate(normalized, out var date);
        var type = IncomeRx.IsMatch(normalized) ? 0 : 1;
        var amount = ExtractAmount(lines);
        var merchant = ExtractMerchant(lines);
        var (categoryId, categoryName) = GuessCategory(normalized, categories, type);

        var warnings = new List<string>();
        if (amount is null) warnings.Add("Сумма не найдена — проверьте и заполните вручную.");
        if (!dateFound) warnings.Add("Дата не найдена — подставлена текущая.");
        if (categoryId is null) warnings.Add("Категория не распознана — выберите вручную.");

        var confidence = 0.2;
        if (amount is not null) confidence += 0.4;
        if (dateFound) confidence += 0.15;
        if (merchant is not null) confidence += 0.1;
        if (categoryId is not null) confidence += 0.15;
        confidence = Math.Round(Math.Min(0.98, confidence), 2);
        if (confidence < 0.5) warnings.Add("Низкая уверенность распознавания — проверьте поля перед сохранением.");

        return new ReceiptParseResult(
            Amount: amount,
            Date: date.ToString("yyyy-MM-dd"),
            Type: type,
            TypeName: type == 0 ? "Доход" : "Расход",
            CategoryId: categoryId,
            CategoryName: categoryName,
            Merchant: merchant,
            Comment: merchant ?? "",
            Confidence: confidence,
            Warnings: warnings,
            Lines: lines.Take(120).ToList(),
            RawText: normalized.Trim().Length > 6000 ? normalized.Trim()[..6000] : normalized.Trim());
    }

    private static string NormalizeLine(string line)
        => Regex.Replace(line.Replace('\u00A0', ' '), @"[ \t]+", " ").Trim();

    private static decimal? ExtractAmount(IReadOnlyList<string> lines)
    {
        foreach (var keyword in StrongAmountKeywords)
        {
            foreach (var line in lines)
            {
                if (!line.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("ндс", StringComparison.OrdinalIgnoreCase)) continue;

                var values = NumberRx.Matches(line)
                    .Select(m => ParseAmount(m.Value))
                    .Where(v => v is not null)
                    .Select(v => v!.Value)
                    .ToList();
                if (values.Count > 0) return values[^1];
            }
        }

        // Резервный путь: самое крупное число, но без дат/длинных номеров карт.
        var candidates = new List<decimal>();
        foreach (var line in lines)
        {
            var clean = LongDigitsRx.Replace(line, " ");
            clean = IsoDateRx.Replace(clean, " ");
            clean = DotDateRx.Replace(clean, " ");
            foreach (Match match in NumberRx.Matches(clean))
            {
                var value = ParseAmount(match.Value);
                if (value is not null) candidates.Add(value.Value);
            }
        }

        return candidates.Count > 0 ? candidates.Max() : null;
    }

    private static decimal? ParseAmount(string raw)
    {
        var s = raw.Replace(" ", "").Replace("\u00A0", "");
        if (s.Length == 0 || s.Count(char.IsDigit) > 9) return null;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            if (lastComma > lastDot) s = s.Replace(".", "").Replace(',', '.');
            else s = s.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            s = s.Replace(',', '.');
        }
        else if (s.Count(c => c == '.') > 1 || Regex.IsMatch(s, @"^\d{1,3}\.\d{3}$"))
        {
            s = s.Replace(".", "");
        }

        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return value is > 0 and <= 10_000_000 ? value : null;
    }

    private static bool TryExtractDate(string text, out DateOnly date)
    {
        var iso = IsoDateRx.Match(text);
        if (iso.Success
            && int.TryParse(iso.Groups[1].Value, out var y)
            && int.TryParse(iso.Groups[2].Value, out var m)
            && int.TryParse(iso.Groups[3].Value, out var d)
            && TryCreateDate(y, m, d, out date))
        {
            return true;
        }

        foreach (Match match in DotDateRx.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out var day)) continue;
            if (!int.TryParse(match.Groups[2].Value, out var month)) continue;
            if (!int.TryParse(match.Groups[3].Value, out var year)) continue;
            if (year < 100) year += 2000;
            if (TryCreateDate(year, month, day, out date)) return true;
        }

        var lower = text.ToLowerInvariant();
        for (var i = 0; i < RussianMonths.Length; i++)
        {
            var monthName = RussianMonths[i];
            var idx = lower.IndexOf(monthName, StringComparison.Ordinal);
            if (idx <= 0) continue;

            var before = lower[..idx].TrimEnd();
            var dayMatch = Regex.Match(before, @"(\d{1,2})$");
            if (!dayMatch.Success || !int.TryParse(dayMatch.Groups[1].Value, out var day)) continue;

            var after = lower[(idx + monthName.Length)..].TrimStart();
            var yearMatch = Regex.Match(after, @"^(\d{4})");
            var year = yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsedYear)
                ? parsedYear
                : DateTime.Today.Year;
            if (TryCreateDate(year, i + 1, day, out date)) return true;
        }

        date = DateOnly.FromDateTime(DateTime.Today);
        return false;
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return year is >= 2000 and <= 2100;
        }
        catch
        {
            date = DateOnly.FromDateTime(DateTime.Today);
            return false;
        }
    }

    private static string? ExtractMerchant(IReadOnlyList<string> lines)
    {
        string? firstCandidate = null;

        foreach (var line in lines)
        {
            if (line.Length < 3 || line.Length > 100) continue;
            if (SkipMerchantRx.IsMatch(line)) continue;
            if (line.Count(char.IsLetter) < 3) continue;
            if (line.Contains("итог", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("сумма", StringComparison.OrdinalIgnoreCase)) continue;

            if (LegalFormRx.IsMatch(line))
                return CleanMerchant(line);

            firstCandidate ??= CleanMerchant(line);
        }

        return firstCandidate;
    }

    private static string CleanMerchant(string line)
    {
        var cleaned = line.Trim(' ', '-', '—', ':', ';', '.', ',', '"', '\'', '«', '»');
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static (int? Id, string? Name) GuessCategory(
        string text, IReadOnlyList<CategoryRef> categories, int type)
    {
        var available = categories.Where(c => (int)c.Type == type).ToList();
        if (available.Count == 0) return (null, null);

        // 1. Прямое совпадение с названием категории.
        var direct = available
            .Where(c => !string.IsNullOrWhiteSpace(c.Name)
                        && text.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Name.Length)
            .FirstOrDefault();
        if (direct is not null) return (direct.Id, direct.Name);

        // 2. Подсказки по словам (пятёрочка → Продукты и т.п.).
        foreach (var (hintName, keywords) in CategoryHints)
        {
            if (!keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

            var category = available.FirstOrDefault(c =>
                c.Name.Contains(hintName, StringComparison.OrdinalIgnoreCase)
                || hintName.Contains(c.Name, StringComparison.OrdinalIgnoreCase));
            if (category is not null) return (category.Id, category.Name);
        }

        return (null, null);
    }
}
