using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Мониторинг инвестиций и сравнение с накопительным счётом (Задача 6).</summary>
public class InvestmentCalculatorTests
{
    private static InvestmentCalculator.HoldingInput Fund(string name, decimal qty, decimal avg, decimal price) =>
        new(name, name, AssetType.Fund, qty, avg, price, "RUB");

    [Fact]
    public void Summarize_Fund_CalculatesInvestedValueAndPnl()
    {
        var s = InvestmentCalculator.Summarize(Fund("Вечный портфель", 100, 10m, 12m));

        Assert.Equal(1_000m, s.Invested);
        Assert.Equal(1_200m, s.Value);
        Assert.Equal(200m, s.Pnl);
        Assert.Equal(20m, s.PnlPct);
    }

    [Fact]
    public void Summarize_CashHolding_QuantityIsAmount()
    {
        var s = InvestmentCalculator.Summarize(
            new InvestmentCalculator.HoldingInput("Деньги копилки", "", AssetType.Cash, 50_000m, 0m, 0m, "RUB"));

        Assert.Equal(50_000m, s.Invested);
        Assert.Equal(50_000m, s.Value);
        Assert.Equal(0m, s.Pnl);
    }

    [Fact]
    public void SummarizePortfolio_SplitsCashAndInvestedParts()
    {
        var holdings = new[]
        {
            new InvestmentCalculator.HoldingInput("Деньги копилки", "", AssetType.Cash, 20_000m, 0m, 0m, "RUB"),
            Fund("Фонд", 100, 10m, 12m),
        };

        var s = InvestmentCalculator.SummarizePortfolio(holdings, days: 365);

        Assert.Equal(21_000m, s.Invested);
        Assert.Equal(21_200m, s.Value);
        Assert.Equal(20_000m, s.CashValue);
        Assert.Equal(1_200m, s.InvestedValue);
        Assert.Equal(200m, s.Pnl);
    }

    [Fact]
    public void SummarizePortfolio_ComparesWithSavingsAccount()
    {
        // 100 000 ₽ под 6 % годовых за 365 дней = 6 000 ₽, под 8 % = 8 000 ₽
        var holdings = new[] { Fund("Фонд", 1_000, 100m, 100m) };

        var s = InvestmentCalculator.SummarizePortfolio(holdings, days: 365);

        Assert.Equal(100_000m, s.Invested);
        Assert.Equal(6_000m, s.SavingsAt6);
        Assert.Equal(8_000m, s.SavingsAt8);
        // без роста цены инвестиции «проиграли» копилке на 8 000 ₽
        Assert.Equal(-8_000m, s.RiskPremium);
    }

    [Fact]
    public void SummarizePortfolio_RiskPremiumIsProfitOverSavingsPro()
    {
        // 100 000 ₽ → 120 000 ₽ за год: доход 20 000 ₽ против 8 000 ₽ у копилки Pro
        var holdings = new[] { Fund("Фонд", 1_000, 100m, 120m) };

        var s = InvestmentCalculator.SummarizePortfolio(holdings, days: 365);

        Assert.Equal(20_000m, s.Pnl);
        Assert.Equal(12_000m, s.RiskPremium);
        // налог 13 % от положительного дохода
        Assert.Equal(2_600m, s.TaxEstimate);
    }

    [Fact]
    public void SummarizePortfolio_EmptyPortfolio_ReturnsZeroes()
    {
        var s = InvestmentCalculator.SummarizePortfolio(Array.Empty<InvestmentCalculator.HoldingInput>(), days: 30);

        Assert.Equal(0m, s.Invested);
        Assert.Equal(0m, s.Value);
        Assert.Equal(0m, s.Pnl);
        Assert.Equal(0m, s.RiskPremium);
        Assert.Empty(s.Holdings);
    }

    [Fact]
    public void SummarizePortfolio_SeveralPortfolios_SummedTogether()
    {
        var holdings = new[]
        {
            Fund("Фонд А", 100, 10m, 11m),
            Fund("Фонд Б", 50, 20m, 18m),
        };

        var s = InvestmentCalculator.SummarizePortfolio(holdings, days: 180);

        Assert.Equal(2_000m, s.Invested);
        Assert.Equal(2_000m, s.Value); // 1 100 + 900
        Assert.Equal(0m, s.Pnl);
    }

    [Fact]
    public void StrategyHint_DescribesEveryMoneyBoxStrategy()
    {
        foreach (var strategy in Enum.GetValues<MoneyBoxStrategy>())
            Assert.NotEmpty(InvestmentCalculator.StrategyHint(strategy));

        Assert.Contains("Денежный рынок", InvestmentCalculator.StrategyHint(MoneyBoxStrategy.AlwaysPlus));
        Assert.Contains("Вечный портфель", InvestmentCalculator.StrategyHint(MoneyBoxStrategy.Balanced));
        Assert.Contains("АСВ", InvestmentCalculator.RiskNote(MoneyBoxStrategy.Balanced));
        Assert.Contains("АСВ", InvestmentCalculator.RiskNote(MoneyBoxStrategy.AlwaysPlus));
    }

    [Fact]
    public void SavingsRates_MatchTbankConditions()
    {
        Assert.Equal(6m, InvestmentCalculator.SavingsRate);
        Assert.Equal(8m, InvestmentCalculator.SavingsRatePro);
        Assert.Equal(13m, InvestmentCalculator.TaxRate);
    }
}
