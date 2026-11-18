using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Расчёты по обязательным платежам (Задача 3): периодичность, даты и статусы.
/// Чистые функции вынесены отдельно, чтобы покрывать их тестами без БД.
/// </summary>
public static class ObligationCalculator
{
    /// <summary>Подпись периодичности для карточки и списка.</summary>
    public static string PeriodicityName(ObligationPeriodicity period) => period switch
    {
        ObligationPeriodicity.OneTime => "Разово",
        ObligationPeriodicity.Monthly => "Ежемесячно",
        ObligationPeriodicity.Quarterly => "Ежеквартально",
        ObligationPeriodicity.Yearly => "Ежегодно",
        ObligationPeriodicity.Custom => "Произвольно",
        _ => "Платёж",
    };

    /// <summary>Просрочен ли платёж: активен и дата следующей оплаты уже прошла.</summary>
    public static bool IsOverdue(ObligationStatus status, DateOnly nextPaymentDate, DateOnly today)
        => status == ObligationStatus.Active && nextPaymentDate < today;

    /// <summary>Подпись статуса: активен / просрочен / на паузе / завершён.</summary>
    public static string StatusName(ObligationStatus status, DateOnly nextPaymentDate, DateOnly today)
        => IsOverdue(status, nextPaymentDate, today) ? "Просрочен"
            : status switch
            {
                ObligationStatus.Paused => "На паузе",
                ObligationStatus.Completed => "Завершён",
                _ => "Активен",
            };

    /// <summary>
    /// Дата следующего платежа после оплаты текущего. Для периодичных платежей
    /// сдвигаемся на один период от даты по графику — если платёж был просрочен,
    /// следующая дата останется в прошлом (неоплаченные периоды не пропадают).
    /// </summary>
    public static DateOnly Advance(
        DateOnly from, ObligationPeriodicity period, int? paymentDay, int? intervalDays)
    {
        switch (period)
        {
            case ObligationPeriodicity.OneTime:
                return from;
            case ObligationPeriodicity.Monthly:
                return AddMonths(from, 1, paymentDay);
            case ObligationPeriodicity.Quarterly:
                return AddMonths(from, 3, paymentDay);
            case ObligationPeriodicity.Yearly:
                return AddMonths(from, 12, paymentDay);
            case ObligationPeriodicity.Custom:
                return from.AddDays(Math.Max(1, intervalDays ?? 1));
            default:
                return from;
        }
    }

    /// <summary>Сдвиг на месяцы с сохранением дня платежа (для 31-го числа — последний день месяца).</summary>
    private static DateOnly AddMonths(DateOnly from, int months, int? paymentDay)
    {
        var target = from.AddMonths(months);
        if (paymentDay is not int day) return target;
        var lastDay = DateTime.DaysInMonth(target.Year, target.Month);
        return new DateOnly(target.Year, target.Month, Math.Clamp(day, 1, lastDay));
    }
}

/// <summary>
/// Обязательные платежи (Задача 3): CRUD, факты оплаты и привязка к долгу.
///
/// Решения от 18.11.2026:
/// - сам платёж — план и в бюджет не попадает; движение денег появляется только
///   при создании операции галочкой в факте оплаты (обычный расход с категорией);
/// - привязка к долгу (DebtId) пока только информационная: автоматического изменения
///   остатка долга нет — это пересечение сущностей из Задачи 4.
/// </summary>
public sealed class ObligationService
{
    /// <summary>Категория, если у платежа не указана своя.</summary>
    public const string FallbackCategoryName = "Обязательные платежи";

    private readonly AppDbContext _db;
    public ObligationService(AppDbContext db) => _db = db;

    /// <summary>Данные обязательного платежа для создания или изменения.</summary>
    public sealed record ObligationInput(
        string? Name, string? Payee, int? CategoryId, decimal Amount,
        int Periodicity, DateOnly? NextPaymentDate, int? PaymentDay,
        int? IntervalDays, int? AccountId, bool Remind, int? DebtId, string? Note);

    /// <summary>
    /// Факт оплаты: сумму можно переопределить, операция создаётся по галочке;
    /// для платежа, связанного с долгом, можно уменьшить остаток долга (Задача 4).
    /// </summary>
    public sealed record PaymentInput(
        decimal? Amount, DateOnly? Date, string? Note, bool CreateTransaction,
        int? AccountId, bool ReduceDebt = false);

    /// <summary>Список платежей: active (по умолчанию), paused, completed, all.</summary>
    public async Task<List<Obligation>> ListAsync(int userId, string? filter)
    {
        var query = _db.Obligations
            .Include(o => o.Account)
            .Include(o => o.Category)
            .Include(o => o.Debt)
            .Include(o => o.Payments)
            .Where(o => o.UserId == userId);

        query = (filter ?? "active").Trim().ToLowerInvariant() switch
        {
            "paused" => query.Where(o => o.Status == ObligationStatus.Paused),
            "completed" => query.Where(o => o.Status == ObligationStatus.Completed),
            "all" => query,
            _ => query.Where(o => o.Status == ObligationStatus.Active),
        };

        return await query
            .OrderBy(o => o.Status)
            .ThenBy(o => o.NextPaymentDate)
            .ThenByDescending(o => o.Id)
            .ToListAsync();
    }

    /// <summary>Один платёж с историей оплат (только свой).</summary>
    public Task<Obligation?> GetAsync(int userId, int obligationId) =>
        _db.Obligations
            .Include(o => o.Account)
            .Include(o => o.Category)
            .Include(o => o.Debt)
            .Include(o => o.Payments.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id))
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);

    /// <summary>Создаёт обязательный платёж.</summary>
    public async Task<(bool Ok, string? Error, Obligation? Obligation)> CreateAsync(
        int userId, ObligationInput input)
    {
        var violation = await ValidateAsync(userId, input);
        if (violation is not null) return (false, violation, null);

        var obligation = new Obligation
        {
            UserId = userId,
            Name = input.Name!.Trim(),
            Payee = input.Payee!.Trim(),
            CategoryId = input.CategoryId,
            Amount = input.Amount,
            Periodicity = (ObligationPeriodicity)input.Periodicity,
            NextPaymentDate = input.NextPaymentDate ?? DateOnly.FromDateTime(DateTime.Today),
            PaymentDay = input.PaymentDay,
            IntervalDays = input.IntervalDays,
            AccountId = input.AccountId,
            Remind = input.Remind,
            DebtId = input.DebtId,
            Note = input.Note?.Trim() ?? "",
            Status = ObligationStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Obligations.Add(obligation);
        await _db.SaveChangesAsync();
        return (true, null, obligation);
    }

    /// <summary>Изменяет обязательный платёж.</summary>
    public async Task<(bool Ok, string? Error)> UpdateAsync(
        int userId, int obligationId, ObligationInput input)
    {
        var obligation = await _db.Obligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);
        if (obligation is null) return (false, "Платёж не найден");

        var violation = await ValidateAsync(userId, input);
        if (violation is not null) return (false, violation);

        obligation.Name = input.Name!.Trim();
        obligation.Payee = input.Payee!.Trim();
        obligation.CategoryId = input.CategoryId;
        obligation.Amount = input.Amount;
        obligation.Periodicity = (ObligationPeriodicity)input.Periodicity;
        obligation.NextPaymentDate = input.NextPaymentDate ?? obligation.NextPaymentDate;
        obligation.PaymentDay = input.PaymentDay;
        obligation.IntervalDays = input.IntervalDays;
        obligation.AccountId = input.AccountId;
        obligation.Remind = input.Remind;
        obligation.DebtId = input.DebtId;
        obligation.Note = input.Note?.Trim() ?? "";
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>
    /// Отмечает оплату: добавляет факт, создаёт операцию по галочке и сдвигает
    /// дату следующего платежа на один период. Разовые платежи завершаются.
    /// </summary>
    public async Task<(bool Ok, string? Error)> PayAsync(
        int userId, int obligationId, PaymentInput input)
    {
        var obligation = await _db.Obligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);
        if (obligation is null) return (false, "Платёж не найден");
        if (obligation.Status == ObligationStatus.Paused)
            return (false, "Платёж на паузе — сначала возобновите его");
        if (obligation.Status == ObligationStatus.Completed)
            return (false, "Платёж уже завершён");

        var amount = input.Amount ?? obligation.Amount;
        if (amount <= 0) return (false, "Сумма должна быть больше нуля");
        if (decimal.Round(amount, 2) != amount)
            return (false, "Укажите сумму с точностью до копеек");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var date = input.Date ?? today;
        if (date > today) return (false, "Дата оплаты не может быть в будущем");

        // Связанный долг: оплата может уменьшать остаток (Задача 4).
        Debt? debtForPayment = null;
        if (input.ReduceDebt)
        {
            if (obligation.DebtId is null)
                return (false, "Платёж не связан с долгом");
            debtForPayment = await _db.Debts
                .FirstOrDefaultAsync(d => d.Id == obligation.DebtId && d.UserId == userId);
            if (debtForPayment is null) return (false, "Долг не найден");
            if (debtForPayment.RemainingAmount <= 0)
                return (false, "Долг уже погашен — остаток нулевой");
            if (amount > debtForPayment.RemainingAmount)
                return (false, "Сумма больше текущего остатка долга");
        }

        int? accountIdForBalance = null;
        int? transactionId = null;
        await using var dbTx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (input.CreateTransaction)
            {
                var accountId = input.AccountId ?? obligation.AccountId;
                if (accountId is null) return (false, "Выберите счёт для операции");
                var account = await ResolveAccountAsync(userId, accountId);
                if (account is null) return (false, "Счёт не найден");
                if (account.IsArchived) return (false, "Счёт в архиве");

                var category = await ResolveCategoryAsync(userId, obligation.CategoryId);
                if (category is null) category = await EnsureFallbackCategoryAsync(userId);

                var tx = new Transaction
                {
                    UserId = userId,
                    CategoryId = category.Id,
                    Type = TxType.Expense,
                    Amount = amount,
                    Date = date,
                    Comment = $"Обязательный платёж: {obligation.Name}"
                        + (string.IsNullOrWhiteSpace(obligation.Payee) ? "" : $" · {obligation.Payee}"),
                    AccountId = account.Id,
                    IsTransfer = false,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Transactions.Add(tx);
                await _db.SaveChangesAsync();
                transactionId = tx.Id;
                accountIdForBalance = account.Id;
            }

            var plannedDate = obligation.NextPaymentDate;
            var payment = new ObligationPayment
            {
                UserId = userId,
                ObligationId = obligation.Id,
                Amount = amount,
                Date = date,
                PlannedDate = plannedDate,
                Note = input.Note?.Trim() ?? "",
                TransactionId = transactionId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.ObligationPayments.Add(payment);
            await _db.SaveChangesAsync();

            if (debtForPayment is not null)
            {
                // Один платёж = одно движение денег. Операция (если создавалась) остаётся
                // только у факта оплаты; у события долга TransactionId не заполняем,
                // чтобы не дублировать сумму в журнале и балансе.
                var ev = new DebtEvent
                {
                    UserId = userId,
                    DebtId = debtForPayment.Id,
                    Kind = DebtEventKind.Repayment,
                    Amount = amount,
                    Date = date,
                    Note = $"Оплата платежа: {obligation.Name}",
                    CreatedAt = DateTime.UtcNow,
                };
                _db.DebtEvents.Add(ev);
                await _db.SaveChangesAsync();
                payment.DebtEventId = ev.Id;
                await _db.SaveChangesAsync();
            }

            if (obligation.Periodicity == ObligationPeriodicity.OneTime)
            {
                obligation.Status = ObligationStatus.Completed;
            }
            else
            {
                obligation.NextPaymentDate = ObligationCalculator.Advance(
                    plannedDate, obligation.Periodicity, obligation.PaymentDay, obligation.IntervalDays);
            }
            await _db.SaveChangesAsync();

            if (debtForPayment is not null)
                await DebtService.RecalcRemainingAsync(_db, debtForPayment.Id);
            if (accountIdForBalance is int accId)
                await AccountBalanceService.RecalcAsync(_db, new[] { accId });

            await dbTx.CommitAsync();
        }
        catch
        {
            await dbTx.RollbackAsync();
            throw;
        }

        return (true, null);
    }

    /// <summary>
    /// Удаляет факт оплаты и связанную операцию. Если это была последняя оплата,
    /// дата следующего платежа возвращается на оплаченную дату по графику.
    /// </summary>
    public async Task<(bool Ok, string? Error)> DeletePaymentAsync(
        int userId, int obligationId, int paymentId)
    {
        var payment = await _db.ObligationPayments
            .Include(p => p.Transaction)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.ObligationId == obligationId);
        if (payment is null) return (false, "Факт оплаты не найден");

        var obligation = await _db.Obligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);
        if (obligation is null) return (false, "Платёж не найден");

        int? balanceAccountId = null;
        if (payment.Transaction is Transaction tx)
        {
            balanceAccountId = tx.AccountId;
            _db.Transactions.Remove(tx);
        }
        // Событие возврата по долгу живой только вместе с фактом оплаты (Задача 4).
        DebtEvent? linkedEvent = payment.DebtEventId is int eventId
            ? await _db.DebtEvents.FirstOrDefaultAsync(e => e.Id == eventId)
            : null;
        if (linkedEvent is not null) _db.DebtEvents.Remove(linkedEvent);

        var expectedNext = ObligationCalculator.Advance(
            payment.PlannedDate, obligation.Periodicity, obligation.PaymentDay, obligation.IntervalDays);
        var wasLastPayment = obligation.Status == ObligationStatus.Completed
            || obligation.NextPaymentDate == expectedNext;
        if (wasLastPayment)
        {
            obligation.NextPaymentDate = payment.PlannedDate;
            obligation.Status = ObligationStatus.Active;
        }

        _db.ObligationPayments.Remove(payment);
        await _db.SaveChangesAsync();
        if (linkedEvent is not null)
            await DebtService.RecalcRemainingAsync(_db, linkedEvent.DebtId);
        if (balanceAccountId is int accId)
            await AccountBalanceService.RecalcAsync(_db, new[] { accId });

        return (true, null);
    }

    /// <summary>Ставит платёж на паузу, возобновляет или завершает.</summary>
    public async Task<(bool Ok, string? Error)> SetStatusAsync(
        int userId, int obligationId, ObligationStatus status)
    {
        var obligation = await _db.Obligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);
        if (obligation is null) return (false, "Платёж не найден");
        if (!Enum.IsDefined(status)) return (false, "Неизвестный статус");

        obligation.Status = status;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Удаляет платёж, историю оплат и связанные операции.</summary>
    public async Task<(bool Ok, string? Error)> DeleteAsync(int userId, int obligationId)
    {
        var obligation = await _db.Obligations
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.UserId == userId);
        if (obligation is null) return (false, "Платёж не найден");

        var txIds = obligation.Payments.Where(p => p.TransactionId != null)
            .Select(p => p.TransactionId!.Value).ToList();
        if (txIds.Count > 0)
        {
            var txs = await _db.Transactions.Where(t => txIds.Contains(t.Id)).ToListAsync();
            _db.Transactions.RemoveRange(txs);
        }

        // Удаляем события возвратов, созданные оплатами этого платежа, и пересчитываем долги.
        var eventIds = obligation.Payments.Where(p => p.DebtEventId != null)
            .Select(p => p.DebtEventId!.Value).ToList();
        var linkedEvents = eventIds.Count > 0
            ? await _db.DebtEvents.Where(e => eventIds.Contains(e.Id)).ToListAsync()
            : [];
        var affectedDebtIds = linkedEvents.Select(e => e.DebtId).Distinct().ToList();
        _db.DebtEvents.RemoveRange(linkedEvents);

        _db.Obligations.Remove(obligation);
        await _db.SaveChangesAsync();

        foreach (var debtId in affectedDebtIds)
            await DebtService.RecalcRemainingAsync(_db, debtId);
        if (txIds.Count > 0)
            await AccountBalanceService.RecalcAsync(_db, await UserAccountIdsAsync(userId));

        return (true, null);
    }

    // ---------- Вспомогательные методы ----------

    private async Task<string?> ValidateAsync(int userId, ObligationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            return "Название платежа обязательно";
        if (string.IsNullOrWhiteSpace(input.Payee))
            return "Укажите получателя платежа";
        if (input.Amount <= 0)
            return "Сумма платежа должна быть больше нуля";
        if (decimal.Round(input.Amount, 2) != input.Amount)
            return "Укажите сумму с точностью до копеек";
        if (!Enum.IsDefined((ObligationPeriodicity)input.Periodicity))
            return "Неизвестная периодичность";

        var period = (ObligationPeriodicity)input.Periodicity;
        if (period == ObligationPeriodicity.Custom && (input.IntervalDays is null or < 1 or > 3650))
            return "Укажите интервал от 1 до 3650 дней";
        if (input.PaymentDay is int day && day is < 1 or > 31)
            return "День платежа должен быть от 1 до 31";

        if (input.CategoryId is int categoryId
            && await _db.Categories.CountAsync(c => c.Id == categoryId && c.UserId == userId) == 0)
            return "Категория не найдена";
        if (input.AccountId is int accountId)
        {
            var account = await ResolveAccountAsync(userId, accountId);
            if (account is null) return "Счёт не найден";
        }
        if (input.DebtId is int debtId)
        {
            var debt = await _db.Debts.FirstOrDefaultAsync(d => d.Id == debtId && d.UserId == userId);
            if (debt is null) return "Долг не найден";
            // Обязательный платёж — это то, что пользователь платит. Связь с долгом
            // «мне должны» не имеет смысла: там деньги приходят и учитываются событиями долга.
            if (debt.Direction != DebtDirection.IOwe)
                return "Платёж можно связать только с долгом «я должен»";
        }

        return null;
    }

    private Task<Account?> ResolveAccountAsync(int userId, int? accountId) =>
        accountId is int id
            ? _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId)
            : Task.FromResult<Account?>(null);

    private Task<Category?> ResolveCategoryAsync(int userId, int? categoryId) =>
        categoryId is int id
            ? _db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId)
            : Task.FromResult<Category?>(null);

    private async Task<List<int>> UserAccountIdsAsync(int userId) =>
        await _db.Accounts.Where(a => a.UserId == userId).Select(a => a.Id).ToListAsync();

    /// <summary>Категория «Обязательные платежи» создаётся при первом использовании.</summary>
    private async Task<Category> EnsureFallbackCategoryAsync(int userId)
    {
        var existing = await _db.Categories
            .FirstOrDefaultAsync(c => c.UserId == userId
                && c.Name == FallbackCategoryName && c.Type == TxType.Expense);
        if (existing is not null) return existing;

        var category = new Category
        {
            UserId = userId,
            Name = FallbackCategoryName,
            Type = TxType.Expense,
            Color = "#dd6b20",
            IsSystem = true,
        };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }
}
