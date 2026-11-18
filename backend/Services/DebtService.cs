using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Расчёты по долгам (Задача 2): остаток, статус и подписи.
/// Вынесены в чистые функции, чтобы покрывать их тестами без БД.
/// </summary>
public static class DebtCalculator
{
    /// <summary>Подпись направления долга.</summary>
    public static string DirectionName(DebtDirection direction) => direction switch
    {
        DebtDirection.IOwe => "Я должен",
        DebtDirection.OwedToMe => "Мне должны",
        _ => "Долг",
    };

    /// <summary>Подпись типа события истории.</summary>
    public static string KindName(DebtEventKind kind) => kind switch
    {
        DebtEventKind.Issued => "Возникновение",
        DebtEventKind.Repayment => "Возврат",
        DebtEventKind.InterestAccrued => "Начислены проценты",
        DebtEventKind.InterestPaid => "Оплачены проценты",
        DebtEventKind.Adjustment => "Корректировка",
        DebtEventKind.WriteOff => "Списание",
        _ => "Событие",
    };

    /// <summary>
    /// Влияние события на остаток долга: положительное — остаток растёт,
    /// отрицательное — уменьшается. Событие «возникновение» уже учтено
    /// в основной сумме и отдельного влияния не имеет.
    /// </summary>
    public static decimal Effect(DebtEventKind kind, decimal amount) => kind switch
    {
        DebtEventKind.Issued => 0m,
        DebtEventKind.Repayment => -Math.Abs(amount),
        DebtEventKind.InterestAccrued => Math.Abs(amount),
        DebtEventKind.InterestPaid => -Math.Abs(amount),
        DebtEventKind.Adjustment => amount, // сумма со знаком
        DebtEventKind.WriteOff => -Math.Abs(amount),
        _ => 0m,
    };

    /// <summary>Остаток долга по основной сумме и истории событий (не ниже нуля).</summary>
    public static decimal CalculateRemaining(
        decimal principal, IEnumerable<(DebtEventKind Kind, decimal Amount)> events)
    {
        var remaining = principal;
        foreach (var e in events) remaining += Effect(e.Kind, e.Amount);
        return Math.Max(0m, decimal.Round(remaining, 2));
    }

    /// <summary>Просрочен ли долг: активен, есть остаток и срок уже прошёл.</summary>
    public static bool IsOverdue(DebtStatus status, decimal remaining, DateOnly? dueDate, DateOnly today)
        => status == DebtStatus.Active && remaining > 0 && dueDate is DateOnly due && due < today;

    /// <summary>Подпись статуса для карточки: активен / просрочен / закрыт.</summary>
    public static string StatusName(DebtStatus status, decimal remaining, DateOnly? dueDate, DateOnly today)
        => IsOverdue(status, remaining, dueDate, today) ? "Просрочен"
            : status == DebtStatus.Closed || remaining <= 0 ? "Закрыт"
            : "Активен";
}

/// <summary>
/// Долги (Задача 2): CRUD и история событий.
///
/// Решения от 18.11.2026:
/// - долги не влияют на доходы/расходы и сводку бюджета — отдельный учёт;
/// - при возврате/оплате процентов операцию по счёту можно создать галочкой;
/// - остаток всегда пересчитывается из основной суммы и истории событий.
/// </summary>
public sealed class DebtService
{
    public const string CategoryName = "Долги";

    private readonly AppDbContext _db;
    public DebtService(AppDbContext db) => _db = db;

    /// <summary>Данные долга для создания или изменения.</summary>
    public sealed record DebtInput(
        int Direction, string? Counterparty, string? Name, decimal PrincipalAmount,
        string? Currency, DateOnly? IssuedAt, DateOnly? DueDate, decimal InterestRate,
        string? Terms, int? AccountId, string? Note);

    /// <summary>Новое событие истории долга.</summary>
    public sealed record DebtEventInput(
        int Kind, decimal Amount, DateOnly? Date, string? Note,
        bool CreateTransaction, int? AccountId);

    /// <summary>Список долгов: active / closed / all. scopeIds — охват «семья».</summary>
    public async Task<List<Debt>> ListAsync(
        int userId, string? filter, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var query = _db.Debts
            .Include(d => d.Account)
            .Include(d => d.User)
            .Include(d => d.Events)
            .Include(d => d.Obligations)
            .Where(d => ids.Contains(d.UserId));

        query = (filter ?? "active").Trim().ToLowerInvariant() switch
        {
            "closed" => query.Where(d => d.Status == DebtStatus.Closed || d.RemainingAmount <= 0),
            "all" => query,
            _ => query.Where(d => d.Status == DebtStatus.Active && d.RemainingAmount > 0),
        };

        return await query
            .OrderBy(d => d.Status)
            .ThenBy(d => d.DueDate ?? DateOnly.MaxValue)
            .ThenByDescending(d => d.Id)
            .ToListAsync();
    }

    /// <summary>Один долг с полной историей событий в рамках охвата.</summary>
    public Task<Debt?> GetAsync(int userId, int debtId, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        return _db.Debts
            .Include(d => d.Account)
            .Include(d => d.User)
            .Include(d => d.Events.OrderByDescending(e => e.Date).ThenByDescending(e => e.Id))
            .Include(d => d.Obligations.OrderBy(o => o.NextPaymentDate))
            .FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
    }

    /// <summary>Создаёт долг и событие возникновения.</summary>
    public async Task<(bool Ok, string? Error, Debt? Debt)> CreateAsync(
        int userId, DebtInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        if (!Enum.IsDefined((DebtDirection)input.Direction))
            return (false, "Неизвестное направление долга", null);
        if (string.IsNullOrWhiteSpace(input.Name))
            return (false, "Название долга обязательно", null);
        if (string.IsNullOrWhiteSpace(input.Counterparty))
            return (false, "Укажите контрагента", null);
        if (input.PrincipalAmount <= 0)
            return (false, "Сумма долга должна быть больше нуля", null);
        if (decimal.Round(input.PrincipalAmount, 2) != input.PrincipalAmount)
            return (false, "Укажите сумму с точностью до копеек", null);
        if (input.InterestRate < 0)
            return (false, "Ставка не может быть отрицательной", null);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var issuedAt = input.IssuedAt ?? today;
        if (issuedAt > today)
            return (false, "Дата возникновения не может быть в будущем", null);
        if (input.DueDate is DateOnly due && due < issuedAt)
            return (false, "Срок возврата раньше даты возникновения", null);

        var account = await ResolveAccountAsync(scopeIds ?? [userId], input.AccountId);
        if (input.AccountId is not null && account is null)
            return (false, "Счёт не найден", null);

        var debt = new Debt
        {
            UserId = userId,
            Direction = (DebtDirection)input.Direction,
            Counterparty = input.Counterparty.Trim(),
            Name = input.Name.Trim(),
            PrincipalAmount = input.PrincipalAmount,
            RemainingAmount = input.PrincipalAmount,
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? "RUB" : input.Currency.Trim().ToUpperInvariant(),
            IssuedAt = issuedAt,
            DueDate = input.DueDate,
            InterestRate = input.InterestRate,
            Terms = string.IsNullOrWhiteSpace(input.Terms) ? null : input.Terms.Trim(),
            AccountId = account?.Id,
            Note = input.Note?.Trim() ?? "",
            Status = DebtStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Debts.Add(debt);
        await _db.SaveChangesAsync();

        _db.DebtEvents.Add(new DebtEvent
        {
            UserId = userId,
            DebtId = debt.Id,
            Kind = DebtEventKind.Issued,
            Amount = debt.PrincipalAmount,
            Date = issuedAt,
            Note = "Возникновение долга",
        });
        await _db.SaveChangesAsync();

        return (true, null, debt);
    }

    /// <summary>Изменяет долг и пересчитывает остаток из истории.</summary>
    public async Task<(bool Ok, string? Error)> UpdateAsync(
        int userId, int debtId, DebtInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var debt = await _db.Debts
            .Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
        if (debt is null) return (false, "Долг не найден");

        if (!Enum.IsDefined((DebtDirection)input.Direction))
            return (false, "Неизвестное направление долга");
        if (string.IsNullOrWhiteSpace(input.Name))
            return (false, "Название долга обязательно");
        if (string.IsNullOrWhiteSpace(input.Counterparty))
            return (false, "Укажите контрагента");
        if (input.PrincipalAmount <= 0)
            return (false, "Сумма долга должна быть больше нуля");
        if (decimal.Round(input.PrincipalAmount, 2) != input.PrincipalAmount)
            return (false, "Укажите сумму с точностью до копеек");
        if (input.InterestRate < 0)
            return (false, "Ставка не может быть отрицательной");
        if (input.DueDate is DateOnly due && due < debt.IssuedAt)
            return (false, "Срок возврата раньше даты возникновения");

        Account? account = null;
        if (input.AccountId is int aid)
        {
            account = await ResolveAccountAsync(ids, aid);
            if (account is null) return (false, "Счёт не найден");
        }

        debt.Direction = (DebtDirection)input.Direction;
        debt.Counterparty = input.Counterparty.Trim();
        debt.Name = input.Name.Trim();
        debt.PrincipalAmount = input.PrincipalAmount;
        debt.Currency = string.IsNullOrWhiteSpace(input.Currency) ? debt.Currency : input.Currency.Trim().ToUpperInvariant();
        debt.IssuedAt = input.IssuedAt ?? debt.IssuedAt;
        debt.DueDate = input.DueDate;
        debt.InterestRate = input.InterestRate;
        debt.Terms = string.IsNullOrWhiteSpace(input.Terms) ? null : input.Terms.Trim();
        debt.AccountId = account?.Id;
        debt.Note = input.Note?.Trim() ?? "";

        debt.RemainingAmount = DebtCalculator.CalculateRemaining(
            debt.PrincipalAmount, debt.Events.Select(e => (e.Kind, e.Amount)));
        // Дата возникновения в истории должна совпадать с карточкой долга.
        var issuedEvent = debt.Events.FirstOrDefault(e => e.Kind == DebtEventKind.Issued);
        if (issuedEvent is not null) issuedEvent.Date = debt.IssuedAt;
        if (debt.RemainingAmount <= 0) debt.Status = DebtStatus.Closed;

        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>
    /// Добавляет событие истории. Для возврата/оплаты процентов можно
    /// одновременно создать операцию по счёту (галочка в форме).
    /// </summary>
    public async Task<(bool Ok, string? Error)> AddEventAsync(
        int userId, int debtId, DebtEventInput input, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var debt = await _db.Debts
            .Include(d => d.Account)
            .Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
        if (debt is null) return (false, "Долг не найден");
        if (debt.RemainingAmount <= 0) return (false, "Долг уже погашен — остаток нулевой");

        if (!Enum.IsDefined((DebtEventKind)input.Kind))
            return (false, "Неизвестный тип события");
        var kind = (DebtEventKind)input.Kind;
        if (kind == DebtEventKind.Issued)
            return (false, "Событие возникновения создаётся автоматически");

        if (kind == DebtEventKind.Adjustment)
        {
            if (input.Amount == 0) return (false, "Укажите сумму корректировки");
        }
        else if (input.Amount <= 0)
        {
            return (false, "Сумма должна быть больше нуля");
        }
        if (decimal.Round(input.Amount, 2) != input.Amount)
            return (false, "Укажите сумму с точностью до копеек");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var date = input.Date ?? today;
        if (date > today) return (false, "Дата события не может быть в будущем");

        // Уменьшающее событие не может превысить остаток долга.
        var effect = DebtCalculator.Effect(kind, input.Amount);
        if (effect < 0 && -effect > debt.RemainingAmount)
            return (false, "Сумма больше текущего остатка долга");

        if (input.CreateTransaction && kind is not (DebtEventKind.Repayment or DebtEventKind.InterestPaid))
            return (false, "Операцию можно создать только для возврата или оплаты процентов");

        int? transactionId = null;
        int? balanceAccountId = null;
        // Событие и связанная операция должны сохраниться вместе.
        await using var dbTx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (input.CreateTransaction)
            {
                var accountId = input.AccountId ?? debt.AccountId;
                if (accountId is null) return (false, "Выберите счёт для операции");
                var account = await ResolveAccountAsync(ids, accountId);
                if (account is null) return (false, "Счёт не найден");
                if (account.IsArchived) return (false, "Счёт в архиве");

                // Я должен: возврат — расход со счёта. Мне должны: возврат — доход.
                var txType = debt.Direction == DebtDirection.IOwe ? TxType.Expense : TxType.Income;
                var category = await EnsureCategoryAsync(userId, txType);
                var tx = new Transaction
                {
                    UserId = userId,
                    CategoryId = category.Id,
                    Type = txType,
                    Amount = input.Amount,
                    Date = date,
                    Comment = TransactionComment(debt, kind),
                    AccountId = account.Id,
                    IsTransfer = false,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Transactions.Add(tx);
                await _db.SaveChangesAsync();
                transactionId = tx.Id;
                balanceAccountId = account.Id;
            }

            _db.DebtEvents.Add(new DebtEvent
            {
                UserId = userId,
                DebtId = debt.Id,
                Kind = kind,
                Amount = input.Amount,
                Date = date,
                Note = input.Note?.Trim() ?? "",
                TransactionId = transactionId,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            await RecalcAsync(debt);
            if (balanceAccountId is int accId)
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

    /// <summary>Удаляет событие, связанную операцию и пересчитывает остаток.</summary>
    public async Task<(bool Ok, string? Error)> DeleteEventAsync(
        int userId, int debtId, int eventId, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var ev = await _db.DebtEvents
            .Include(e => e.Transaction)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DebtId == debtId);
        if (ev is null) return (false, "Событие не найдено");

        var debt = await _db.Debts.FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
        if (debt is null) return (false, "Долг не найден");
        if (ev.Kind == DebtEventKind.Issued)
            return (false, "Событие возникновения удалить нельзя — удалите сам долг");
        // Событие может быть создано оплатой связанного обязательного платежа (Задача 4).
        // Удалять его нужно только вместе с фактом оплаты, иначе остаток разойдётся.
        if (await _db.ObligationPayments.AnyAsync(p => p.DebtEventId == ev.Id))
            return (false, "Событие создано оплатой обязательного платежа — удалите факт оплаты");

        int? balanceAccountId = null;
        if (ev.TransactionId is int txId)
        {
            var tx = ev.Transaction ?? await _db.Transactions.FirstOrDefaultAsync(t => t.Id == txId);
            if (tx is not null)
            {
                balanceAccountId = tx.AccountId;
                _db.Transactions.Remove(tx);
            }
        }
        _db.DebtEvents.Remove(ev);
        await _db.SaveChangesAsync();

        await RecalcAsync(debt);
        if (balanceAccountId is int accId)
            await AccountBalanceService.RecalcAsync(_db, new[] { accId });

        return (true, null);
    }

    /// <summary>Закрывает долг вручную или снова открывает его.</summary>
    public async Task<(bool Ok, string? Error)> SetStatusAsync(
        int userId, int debtId, bool close, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var debt = await _db.Debts.FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
        if (debt is null) return (false, "Долг не найден");

        if (close)
        {
            debt.Status = DebtStatus.Closed;
        }
        else
        {
            if (debt.RemainingAmount <= 0)
                return (false, "Остаток долга нулевой — открывать нечего");
            debt.Status = DebtStatus.Active;
        }

        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Удаляет долг, историю и связанные операции; пересчитывает счета.</summary>
    public async Task<(bool Ok, string? Error)> DeleteAsync(
        int userId, int debtId, IReadOnlyCollection<int>? scopeIds = null)
    {
        var ids = scopeIds ?? [userId];
        var debt = await _db.Debts
            .Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.Id == debtId && ids.Contains(d.UserId));
        if (debt is null) return (false, "Долг не найден");

        var accountIds = new HashSet<int>();
        var txIds = debt.Events.Where(e => e.TransactionId != null)
            .Select(e => e.TransactionId!.Value).ToList();
        if (txIds.Count > 0)
        {
            var txs = await _db.Transactions.Where(t => txIds.Contains(t.Id)).ToListAsync();
            foreach (var tx in txs)
            {
                if (tx.AccountId is int accId) accountIds.Add(accId);
                _db.Transactions.Remove(tx);
            }
        }

        _db.Debts.Remove(debt);
        await _db.SaveChangesAsync();

        if (accountIds.Count > 0)
            await AccountBalanceService.RecalcAsync(_db, accountIds);

        return (true, null);
    }

    /// <summary>
    /// Пересчитывает остаток долга из истории событий. Используется и сервисом долгов,
    /// и сервисом обязательных платежей, когда оплата связанного платежа меняет остаток (Задача 4).
    /// </summary>
    public static async Task RecalcRemainingAsync(
        AppDbContext db, int debtId, CancellationToken ct = default)
    {
        var debt = await db.Debts.FirstOrDefaultAsync(d => d.Id == debtId, ct);
        if (debt is null) return;

        var events = await db.DebtEvents
            .Where(e => e.DebtId == debtId)
            .Select(e => new { e.Kind, e.Amount })
            .ToListAsync(ct);

        debt.RemainingAmount = DebtCalculator.CalculateRemaining(
            debt.PrincipalAmount, events.Select(e => (e.Kind, e.Amount)));
        debt.Status = debt.RemainingAmount <= 0 ? DebtStatus.Closed : DebtStatus.Active;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Пересчитывает остаток и синхронизирует статус.</summary>
    private Task RecalcAsync(Debt debt) => RecalcRemainingAsync(_db, debt.Id);

    private Task<Account?> ResolveAccountAsync(
        IReadOnlyCollection<int> userIds, int? accountId) =>
        accountId is int id
            ? _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && userIds.Contains(a.UserId))
            : Task.FromResult<Account?>(null);

    private static string TransactionComment(Debt debt, DebtEventKind kind)
    {
        var prefix = kind == DebtEventKind.InterestPaid ? "Проценты по долгу" : "Возврат долга";
        return debt.Direction == DebtDirection.IOwe
            ? $"{prefix}: {debt.Name}"
            : $"{prefix} (мне вернули): {debt.Name}";
    }

    /// <summary>Категория «Долги» нужного типа — общая для всей семьи.</summary>
    private Task<Category> EnsureCategoryAsync(int userId, TxType type) =>
        CategoryService.EnsureSystemAsync(_db, userId, CategoryName, type, "#718096");
}
