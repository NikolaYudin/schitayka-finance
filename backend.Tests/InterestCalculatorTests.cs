using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Расчёт доходности вкладов и накопительных счетов (Задача 5).</summary>
public class InterestCalculatorTests
{
    private static readonly DateTime Today = new(2026, 11, 13);

    [Fact]
    public void DailyIncome_NakopiitelnySchet_14Percent()
    {
        // 100 000 ₽ под 14 % годовых: доход за день ≈ 38,36 ₽
        var daily = InterestCalculator.DailyIncome(100_000m, 14m);

        Assert.Equal(38.36m, Math.Round(daily, 2));
    }

    [Fact]
    public void DailyIncome_ZeroBalanceOrRate_ReturnsZero()
    {
        Assert.Equal(0m, InterestCalculator.DailyIncome(0m, 14m));
        Assert.Equal(0m, InterestCalculator.DailyIncome(100_000m, 0m));
    }

    [Fact]
    public void Forecast_NakopiitelnySchet_365Days_GrossEqualsBalanceTimesRate()
    {
        var f = InterestCalculator.Forecast(
            rate: 10m,
            mode: InterestAccrualMode.MonthlyPayout,
            balance: 100_000m,
            months: 12,
            taxRate: 13m,
            today: Today);

        // 100 000 × 10 % = 10 000 ₽ в год (Act/365)
        Assert.Equal(10_000m, Math.Round(f.GrossTotal, 2));
        Assert.Equal(1_300m, Math.Round(f.TaxTotal, 2));
        Assert.Equal(8_700m, Math.Round(f.NetTotal, 2));
        Assert.Equal(10_000m, f.Yearly);
        Assert.Equal(12, f.Rows.Count);
    }

    [Fact]
    public void Forecast_Capitalization_GrowsBalanceAndBeatsPayout()
    {
        var withCapitalization = InterestCalculator.Forecast(
            10m, InterestAccrualMode.DailyToBalance, 100_000m, 12, 13m, today: Today);
        var withPayout = InterestCalculator.Forecast(
            10m, InterestAccrualMode.MonthlyPayout, 100_000m, 12, 13m, today: Today);

        Assert.True(withCapitalization.GrossTotal > withPayout.GrossTotal);
        Assert.True(withCapitalization.Rows[^1].EndBalance > 100_000m);
        Assert.Equal(100_000m, withPayout.Rows[^1].EndBalance);
    }

    [Fact]
    public void Forecast_VkladWithMaturity_InterestAccruedToEndOfTerm()
    {
        var opened = new DateTime(2026, 1, 1);
        var matures = new DateTime(2027, 1, 1); // 365 дней

        var f = InterestCalculator.Forecast(
            rate: 12m,
            mode: InterestAccrualMode.EndOfTerm,
            balance: 100_000m,
            months: 24,
            taxRate: 13m,
            openedAt: opened,
            maturesAt: matures,
            today: Today);

        // 100 000 × 12 % × 365/365 = 12 000 ₽
        Assert.Equal(12_000m, f.GrossTotal);
        Assert.Equal(1_560m, f.TaxTotal);
        Assert.Equal(10_440m, f.NetTotal);
        Assert.Single(f.Rows);
        Assert.Equal(2027, f.Rows[0].Year);
        Assert.Equal(1, f.Rows[0].Month);
    }

    [Fact]
    public void Forecast_VkladMaturityInPast_ReturnsZero()
    {
        var f = InterestCalculator.Forecast(
            12m, InterestAccrualMode.EndOfTerm, 100_000m, 12, 13m,
            openedAt: new DateTime(2025, 1, 1),
            maturesAt: new DateTime(2026, 1, 1),
            today: Today);

        Assert.Equal(0m, f.GrossTotal);
        Assert.Contains("погашен", f.Note);
    }

    [Fact]
    public void Forecast_NoRateOrNoBalance_ReturnsZeroWithNote()
    {
        var noRate = InterestCalculator.Forecast(0m, InterestAccrualMode.MonthlyPayout, 100_000m, 12, 13m, today: Today);
        var noMode = InterestCalculator.Forecast(10m, InterestAccrualMode.None, 100_000m, 12, 13m, today: Today);
        var noBalance = InterestCalculator.Forecast(10m, InterestAccrualMode.MonthlyPayout, 0m, 12, 13m, today: Today);

        Assert.Equal(0m, noRate.GrossTotal);
        Assert.Equal(0m, noMode.GrossTotal);
        Assert.Equal(0m, noBalance.GrossTotal);
        Assert.NotEmpty(noMode.Note);
    }

    [Fact]
    public void Tax_IsThirteenPercentOfGross()
    {
        Assert.Equal(1_300m, InterestCalculator.TaxOf(10_000m, 13m));
        Assert.Equal(0m, InterestCalculator.TaxOf(0m, 13m));
        Assert.Equal(0m, InterestCalculator.TaxOf(-500m, 13m));
    }

    [Fact]
    public void YearlySimple_IsBalanceTimesPercent()
    {
        Assert.Equal(14_000m, InterestCalculator.YearlySimple(100_000m, 14m));
        Assert.Equal(0m, InterestCalculator.YearlySimple(0m, 14m));
    }

    [Fact]
    public void Forecast_MonthlyAverage_IsTwelfthOfYear()
    {
        var f = InterestCalculator.Forecast(12m, InterestAccrualMode.MonthlyPayout, 100_000m, 12, 13m, today: Today);

        Assert.Equal(1_000m, Math.Round(f.MonthlyAvg, 2));
        Assert.Equal(12m, f.Rows.Count);
    }
}
