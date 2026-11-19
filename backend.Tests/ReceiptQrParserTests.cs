using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Локальный разбор QR-строки чека ФНС (B1).</summary>
public class ReceiptQrParserTests
{
    [Fact]
    public void Parse_ValidQr_ReturnsAllFields()
    {
        const string qr = "t=1 057,70&s=1 057,70&fn=9280440300911662&i=227&fp=2872639585&n=3";

        var result = ReceiptQrParser.Parse(qr);

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.Equal(1057.70m, result.Data!.Amount);
        Assert.Equal(1057.70m, result.Data.PositionAmount);
        Assert.Equal("9280440300911662", result.Data.Fn);
        Assert.Equal("227", result.Data.FiscalDocument);
        Assert.Equal("2872639585", result.Data.FiscalSign);
        Assert.Equal("3", result.Data.Shift);
    }

    [Fact]
    public void Parse_LinkWithQuery_ExtractsFields()
    {
        const string qr = "https://example.ru/chek?t=250.00&fn=9960440300813376&i=42&fp=1234";

        var result = ReceiptQrParser.Parse(qr);

        Assert.True(result.Ok);
        Assert.Equal(250m, result.Data!.Amount);
        Assert.Null(result.Data.PositionAmount);
        Assert.Null(result.Data.Shift);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t=abc&fn=9280440300911662&i=227&fp=2872639585")]
    [InlineData("t=100.00&fn=NOT_A_FN&i=227&fp=2872639585")]
    [InlineData("t=100.00&s=100.00")]
    public void Parse_BrokenQr_ReturnsError(string qr)
    {
        var result = ReceiptQrParser.Parse(qr);

        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void FromQr_ProducesReceiptWithAmountAndQrData()
    {
        var qr = ReceiptQrParser.Parse("t=250.00&fn=9960440300813376&i=42&fp=1234").Data!;

        var result = ReceiptParser.FromQr(qr, "t=250.00&fn=9960440300813376&i=42&fp=1234");

        Assert.Equal(250m, result.Amount);
        Assert.Equal(1, result.Type);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"), result.Date);
        Assert.Equal(qr, result.Qr);
        Assert.NotNull(result.Items);
        Assert.Empty(result.Items!);
        Assert.Contains(result.Warnings, w => w.Contains("Категория"));
    }
}
