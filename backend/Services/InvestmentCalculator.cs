using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Мониторинг инвестиций и сравнение с накопительным счётом (Задача 6).
///
/// Ориентиры Т-Банка (tbank.ru/invest/moneybox, tbank.ru/savings):
///   накопительный счёт — 6 % годовых, 8 % с подпиской Pro;
///   Инвесткопилка до выбора стратегии — те же 6/8 %, дальше деньги инвестируются
///   в фонды Т-Капитала («Всегда в плюсе», «Сбалансированная», «Стабильные компании»);
///   налог — НДФЛ 13 %, удерживает брокер; АСВ такие деньги не покрывает.
///
/// Класс не зависит от БД — вся логика тестируется в backend.Tests.
/// </summary>
public static class InvestmentCalculator
{
    /// <summary>Ставка накопительного счёта Т-Банка без подписки Pro, % годовых.</summary>
    public const decimal SavingsRate = 6m;

    /// <summary>Ставка накопительного счёта Т-Банка с подпиской Pro, % годовых.</summary>
    public const decimal SavingsRatePro = 8m;

    /// <summary>НДФЛ на инвестиционный доход, %.</summary>
    public const decimal TaxRate = 13m;

    /// <summary>Исходные данные позиции для расчёта.</summary>
    public record HoldingInput(
        string Name,
        string Ticker,
        AssetType Type,
        decimal Quantity,
        decimal AvgPrice,
        decimal CurrentPrice,
        string Currency,
        int Id = 0);

    /// <summary>Позиция в деньгах.</summary>
    public record HoldingSummary(
        int Id,
        string Name,
        string Ticker,
        AssetType Type,
        decimal Quantity,
        decimal AvgPrice,
        decimal CurrentPrice,
        decimal Invested,
        decimal Value,
        decimal Pnl,
        decimal PnlPct);

    /// <summary>Итог по портфелю + сравнение с копилкой.</summary>
    public record PortfolioSummary(
        decimal Invested,
        decimal Value,
        decimal Pnl,
        decimal PnlPct,
        decimal CashValue,
        decimal InvestedValue,
        int Days,
        decimal SavingsAt6,
        decimal SavingsAt8,
        decimal RiskPremium,
        decimal TaxEstimate,
        IReadOnlyList<HoldingSummary> Holdings);

    /// <summary>Расчёт одной позиции.</summary>
    public static HoldingSummary Summarize(HoldingInput h)
    {
        // Денежная часть копилки (Cash): количество = сумма, цена = 1.
        var isCash = h.Type == AssetType.Cash;
        var avg = isCash ? 1m : h.AvgPrice;
        var price = isCash ? 1m : (h.CurrentPrice > 0 ? h.CurrentPrice : h.AvgPrice);

        var invested = Math.Round(h.Quantity * avg, 2);
        var value = Math.Round(h.Quantity * price, 2);
        var pnl = Math.Round(value - invested, 2);
        var pnlPct = invested > 0m ? Math.Round(pnl / invested * 100m, 2) : 0m;

        return new HoldingSummary(
            h.Id, h.Name, h.Ticker, h.Type, h.Quantity, avg, price, invested, value, pnl, pnlPct);
    }

    /// <summary>
    /// Итог по портфелю. <paramref name="days"/> — срок, за который сравниваем
    /// с накопительным счётом (обычно возраст портфеля).
    /// </summary>
    public static PortfolioSummary SummarizePortfolio(
        IReadOnlyList<HoldingInput> holdings,
        int days,
        DateTime? createdAt = null,
        DateTime? today = null)
    {
        var now = (today ?? DateTime.Today).Date;
        if (createdAt is not null)
        {
            var age = (now - createdAt.Value.Date).Days;
            if (age > days) days = age;
        }
        days = Math.Max(days, 1);

        var summaries = holdings.Select(Summarize).ToList();

        var invested = Math.Round(summaries.Sum(h => h.Invested), 2);
        var value = Math.Round(summaries.Sum(h => h.Value), 2);
        var pnl = Math.Round(value - invested, 2);
        var pnlPct = invested > 0m ? Math.Round(pnl / invested * 100m, 2) : 0m;

        var cashValue = Math.Round(summaries.Where(h => h.Type == AssetType.Cash).Sum(h => h.Value), 2);
        var investedValue = Math.Round(value - cashValue, 2);

        // Сколько дал бы накопительный счёт за тот же срок.
        var savings6 = Math.Round(invested * SavingsRate / 100m * days / InterestCalculator.DaysInYear, 2);
        var savings8 = Math.Round(invested * SavingsRatePro / 100m * days / InterestCalculator.DaysInYear, 2);

        // «Цена риска»: насколько инвестиции обошлись дороже гарантированной копилки.
        var riskPremium = Math.Round(pnl - savings8, 2);

        var tax = pnl > 0m ? Math.Round(pnl * TaxRate / 100m, 2) : 0m;

        return new PortfolioSummary(
            invested, value, pnl, pnlPct, cashValue, investedValue,
            days, savings6, savings8, riskPremium, tax, summaries);
    }

    /// <summary>Ориентировочная доходность стратегии Инвесткопилки для сравнения.</summary>
    public static string StrategyHint(MoneyBoxStrategy strategy) => strategy switch
    {
        MoneyBoxStrategy.AlwaysPlus =>
            "«Всегда в плюсе» — фонд «Денежный рынок»: доход близок к ставке Банка России, риск минимальный.",
        MoneyBoxStrategy.Balanced =>
            "«Сбалансированная» — фонд «Вечный портфель»: акции РФ, золото, долгосрочные облигации и Денежный рынок.",
        MoneyBoxStrategy.StableCompanies =>
            "«Стабильные компании» — фонд «Крупнейшие компании РФ»: доля акций крупных эмитентов, волатильность выше.",
        _ => "Стратегия не выбрана — деньги лежат на накопительном счёте.",
    };

    /// <summary>Короткое предупреждение о рисках для карточки портфеля.</summary>
    public static string RiskNote(MoneyBoxStrategy strategy) => strategy switch
    {
        MoneyBoxStrategy.AlwaysPlus =>
            "Риск ниже среднего: базовый актив — деньги рынка. АСВ не покрывает, НДФЛ 13 % удерживает брокер.",
        _ =>
            "Риск выше среднего: в портфеле есть акции и золото. АСВ не покрывает, НДФЛ 13 % удерживает брокер.",
    };
}
