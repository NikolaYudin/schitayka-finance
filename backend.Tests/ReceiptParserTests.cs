using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

public class ReceiptParserTests
{
    private static readonly CategoryRef[] Categories =
    [
        new(1, "Продукты", TxType.Expense),
        new(2, "Транспорт", TxType.Expense),
        new(3, "Зарплата", TxType.Income),
        new(4, "Подработка", TxType.Income),
    ];

    [Fact]
    public void Parse_ReceiptWithTotal_ReturnsAmountDateMerchantAndCategory()
    {
        const string text = """
        ООО "Пятёрочка"
        Чек № 123
        11.11.2026 18:42
        Хлеб 50,00
        Молоко 89,90
        ИТОГО 1 250,00
        """;

        var result = ReceiptParser.Parse(text, Categories);

        Assert.Equal(1250m, result.Amount);
        Assert.Equal("2026-11-11", result.Date);
        Assert.Equal(1, result.Type);
        Assert.Equal(1, result.CategoryId);
        Assert.Equal("Продукты", result.CategoryName);
        Assert.Contains("Пятёрочка", result.Merchant);
        Assert.True(result.Confidence >= 0.7);
        Assert.Empty(result.Warnings.Where(w => w.Contains("Сумма не найдена")));
    }

    [Fact]
    public void Parse_BankScreenshot_FallbackFindsAmountAndCategory()
    {
        const string text = """
        Списание
        Пятёрочка
        1 250,00 ₽
        11.11.2026
        """;

        var result = ReceiptParser.Parse(text, Categories);

        Assert.Equal(1250m, result.Amount);
        Assert.Equal("2026-11-11", result.Date);
        Assert.Equal(1, result.CategoryId);
    }

    [Fact]
    public void Parse_IncomeText_DetectsIncomeType()
    {
        const string text = """
        Зачисление заработной платы
        Сумма 95 000,00 ₽
        11.11.2026
        """;

        var result = ReceiptParser.Parse(text, Categories);

        Assert.Equal(0, result.Type);
        Assert.Equal(95000m, result.Amount);
    }

    [Fact]
    public void Parse_EmptyText_ReturnsWarningsAndToday()
    {
        var result = ReceiptParser.Parse("", Categories);

        Assert.Null(result.Amount);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"), result.Date);
        Assert.Contains(result.Warnings, w => w.Contains("Сумма не найдена"));
        Assert.Contains(result.Warnings, w => w.Contains("Дата не найдена"));
    }
}
