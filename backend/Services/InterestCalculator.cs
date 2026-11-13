using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Расчёт доходности вкладов и накопительных счетов (Задача 5).
///
/// Формулы (Act/365, как в Т-Банке):
///   доход за день = Остаток × Ставка% / 365
///   накопительный счёт: % ежедневно на остаток, выплата раз в месяц (или капитализация)
///   вклад: сумма × Ставка% × дни срока / 365, выплата в конце срока
///
/// Класс не зависит от БД — вся логика тестируется в backend.Tests.
/// </summary>
public static class InterestCalculator
{
    public const int DaysInYear = 365;

    /// <summary>Дневная ставка в долях (0,14% годовых → 0,00038356).</summary>
    public static decimal DailyFactor(decimal annualRatePercent) =>
        annualRatePercent / 100m / DaysInYear;

    /// <summary>Доход за один день на остаток (без округления — округляем итог периода).</summary>
    public static decimal DailyIncome(decimal balance, decimal annualRatePercent)
    {
        if (balance <= 0m || annualRatePercent <= 0m) return 0m;
        return balance * DailyFactor(annualRatePercent);
    }

    /// <summary>НДФЛ с начисленного дохода, %.</summary>
    public static decimal TaxOf(decimal gross, decimal taxRatePercent) =>
        gross <= 0m ? 0m : Math.Round(gross * taxRatePercent / 100m, 2);

    /// <summary>Сумма за год без учёта дней (для витрины «≈ в год»).</summary>
    public static decimal YearlySimple(decimal balance, decimal annualRatePercent) =>
        balance <= 0m ? 0m : Math.Round(balance * annualRatePercent / 100m, 2);

    /// <summary>
    /// Прогноз доходности по счёту на N месяцев вперёд.
    /// </summary>
    /// <param name="rate">Ставка % годовых</param>
    /// <param name="mode">Режим начисления</param>
    /// <param name="balance">Текущий остаток счёта</param>
    /// <param name="months">Горизонт прогноза, месяцев</param>
    /// <param name="taxRate">Ставка НДФЛ, %</param>
    /// <param name="openedAt">Дата открытия счёта/вклада</param>
    /// <param name="maturesAt">Дата окончания вклада</param>
    /// <param name="today">Опорная дата (для тестов)</param>
    public static InterestForecast Forecast(
        decimal rate,
        InterestAccrualMode mode,
        decimal balance,
        int months = 12,
        decimal taxRate = 13m,
        DateTime? openedAt = null,
        DateTime? maturesAt = null,
        DateTime? today = null)
    {
        var now = (today ?? DateTime.Today).Date;
        months = Math.Clamp(months, 1, 60);

        if (mode == InterestAccrualMode.None || rate <= 0m || balance <= 0m)
        {
            return new InterestForecast(
                rate, mode, balance, 0m, 0m, 0m, 0m, 0m, 0m,
                Array.Empty<InterestForecastRow>(),
                mode == InterestAccrualMode.None
                    ? "Ставка не задана."
                    : "Нет остатка или ставки — доход считать не от чего.");
        }

        // --- Вклад с фиксированным сроком: проценты на сумму в конце срока ---
        if (mode == InterestAccrualMode.EndOfTerm)
        {
            var start = (openedAt ?? now).Date;
            var end = (maturesAt ?? now.AddMonths(months)).Date;
            if (end <= start)
                return new InterestForecast(rate, mode, balance, 0m, 0m, 0m, 0m, 0m, 0m,
                    Array.Empty<InterestForecastRow>(), "Дата окончания вклада уже прошла.");

            var days = (end - start).Days;
            if (end <= today)
                return new InterestForecast(rate, mode, balance, 0m, 0m, 0m, 0m, 0m, 0m,
                    Array.Empty<InterestForecastRow>(), "Вклад уже погашен — проценты начислены ранее.");

            var gross = Math.Round(balance * rate / 100m * days / DaysInYear, 2);
            var tax = TaxOf(gross, taxRate);
            var row = new InterestForecastRow(end.Year, end.Month, gross, tax, gross - tax, balance, balance);
            return new InterestForecast(
                rate, mode, balance, gross, tax, gross - tax,
                YearlySimple(balance, rate), 0m, 0m, new[] { row },
                $"Вклад до {end:dd.MM.yyyy}: {days} дн. под {rate}% годовых.");
        }

        // --- Накопительный счёт: ежедневно на остаток ---
        var current = balance;
        var cursor = (openedAt ?? now).Date;
        var horizon = now.AddMonths(months);
        var rows = new List<InterestForecastRow>();
        decimal grossRawTotal = 0m;

        while (cursor < horizon)
        {
            var monthEnd = cursor.AddMonths(1);
            var segmentEnd = monthEnd < horizon ? monthEnd : horizon;
            var days = (segmentEnd - cursor).Days;
            if (days > 0 && current > 0m)
            {
                var raw = current * rate / 100m * days / DaysInYear;
                grossRawTotal += raw;
                var gross = Math.Round(raw, 2);
                var tax = TaxOf(gross, taxRate);
                rows.Add(new InterestForecastRow(
                    cursor.Year, cursor.Month, gross, tax, gross - tax, current, current));

                // Капитализация: проценты остаются на счёте и сами дают доход.
                if (mode == InterestAccrualMode.DailyToBalance)
                    current = Math.Round(current + gross, 2);
            }
            cursor = segmentEnd;
        }

        var grossTotal = Math.Round(grossRawTotal, 2);
        var taxTotal = TaxOf(grossTotal, taxRate);
        var netTotal = Math.Round(grossTotal - taxTotal, 2);
        var monthlyAvg = rows.Count > 0 ? Math.Round(grossTotal / rows.Count, 2) : 0m;

        var note = mode == InterestAccrualMode.DailyToBalance
            ? "Ежедневное начисление на остаток с капитализацией."
            : "Ежедневное начисление на остаток, выплата раз в месяц.";

        return new InterestForecast(
            rate, mode, balance, grossTotal, taxTotal, netTotal,
            YearlySimple(balance, rate), monthlyAvg, Math.Round(DailyIncome(balance, rate), 2),
            rows, note);
    }
}

/// <summary>Начисления за один месяц прогноза.</summary>
public record InterestForecastRow(
    int Year,
    int Month,
    decimal Gross,
    decimal Tax,
    decimal Net,
    decimal StartBalance,
    decimal EndBalance);

/// <summary>Итог прогноза доходности счёта.</summary>
public record InterestForecast(
    decimal Rate,
    InterestAccrualMode Mode,
    decimal Balance,
    decimal GrossTotal,
    decimal TaxTotal,
    decimal NetTotal,
    decimal Yearly,
    decimal MonthlyAvg,
    decimal Daily,
    IReadOnlyList<InterestForecastRow> Rows,
    string Note);
