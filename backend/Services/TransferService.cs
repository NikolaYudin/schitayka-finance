using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Переводы между своими счетами (Задача 7).
///
/// Перевод — одна бизнес-сущность с двумя операциями:
///   расход на счёте-отправителе + доход на счёте-получателе, обе с IsTransfer = true,
///   поэтому перевод не искажает доходы/расходы и не попадает в аналитику.
/// Отмена перевода удаляет обе операции и пересчитывает остатки обоих счетов.
/// </summary>
public sealed class TransferService
{
    public const string CategoryName = "Перевод";

    private readonly AppDbContext _db;
    public TransferService(AppDbContext db) => _db = db;

    public record CreateResult(bool Ok, string? Error, Transfer? Transfer);

    /// <summary>Создаёт перевод: две операции + запись в журнале переводов.</summary>
    public async Task<CreateResult> CreateAsync(
        int userId, int fromAccountId, int toAccountId, decimal amount, DateOnly date, string? comment)
    {
        if (amount <= 0) return new(false, "Сумма перевода должна быть больше нуля", null);
        if (decimal.Round(amount, 2) != amount) return new(false, "Укажите сумму с точностью до копеек", null);
        if (date > DateOnly.FromDateTime(DateTime.Today)) return new(false, "Дата перевода не может быть в будущем", null);
        if (fromAccountId == toAccountId) return new(false, "Счёт-отправитель и счёт-получатель совпадают", null);

        var from = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == fromAccountId && a.UserId == userId);
        if (from is null) return new(false, "Счёт-отправитель не найден", null);
        if (from.IsArchived) return new(false, "Счёт-отправитель в архиве", null);

        var to = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == toAccountId && a.UserId == userId);
        if (to is null) return new(false, "Счёт-получатель не найден", null);
        if (to.IsArchived) return new(false, "Счёт-получатель в архиве", null);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await RecalcAsync(userId, from.Id, to.Id);
        var available = from.Type == AccountType.CreditCard
            ? Math.Max(0m, from.CreditLimit - from.Balance) : from.Balance;
        if (amount > available) return new(false, "Недостаточно средств на счёте-отправителе", null);
        if (to.Type == AccountType.CreditCard && amount > to.Balance)
            return new(false, "Сумма перевода превышает долг по кредитной карте", null);

        // Категории перевода: системные, создаются при первом использовании.
        var outCategory = await EnsureCategoryAsync(userId, TxType.Expense);
        var inCategory = await EnsureCategoryAsync(userId, TxType.Income);

        var transfer = new Transfer
        {
            UserId = userId,
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = amount,
            Date = date,
            Comment = comment?.Trim() ?? "",
            CreatedAt = DateTime.UtcNow,
        };

        var outTx = new Transaction
        {
            UserId = userId,
            CategoryId = outCategory.Id,
            Type = TxType.Expense,
            Amount = amount,
            Date = date,
            Comment = transfer.Comment.Length > 0 ? transfer.Comment : $"Перевод на «{to.Name}»",
            AccountId = from.Id,
            IsTransfer = true,
        };
        var inTx = new Transaction
        {
            UserId = userId,
            CategoryId = inCategory.Id,
            Type = TxType.Income,
            Amount = amount,
            Date = date,
            Comment = transfer.Comment.Length > 0 ? transfer.Comment : $"Перевод с «{from.Name}»",
            AccountId = to.Id,
            IsTransfer = true,
        };

        _db.Transactions.Add(outTx);
        _db.Transactions.Add(inTx);
        await _db.SaveChangesAsync();

        transfer.FromTxId = outTx.Id;
        transfer.ToTxId = inTx.Id;
        _db.Transfers.Add(transfer);
        await _db.SaveChangesAsync();

        await RecalcAsync(userId, from.Id, to.Id);
        await transaction.CommitAsync();

        return new(true, null, transfer);
    }

    /// <summary>Отменяет перевод: удаляет обе операции и запись, пересчитывает остатки.</summary>
    public async Task<(bool Ok, string? Error)> CancelAsync(int userId, int transferId)
    {
        var transfer = await _db.Transfers
            .Include(t => t.FromAccount)
            .Include(t => t.ToAccount)
            .FirstOrDefaultAsync(t => t.Id == transferId && t.UserId == userId);
        if (transfer is null) return (false, "Перевод не найден");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        if (transfer.FromTxId is int fromTxId)
        {
            var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == fromTxId && t.UserId == userId);
            if (tx is not null) _db.Transactions.Remove(tx);
        }
        if (transfer.ToTxId is int toTxId)
        {
            var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == toTxId && t.UserId == userId);
            if (tx is not null) _db.Transactions.Remove(tx);
        }
        _db.Transfers.Remove(transfer);
        await _db.SaveChangesAsync();

        await RecalcAsync(userId, transfer.FromAccountId, transfer.ToAccountId);
        await transaction.CommitAsync();
        return (true, null);
    }

    /// <summary>
    /// Если операция входит в перевод — отменяем весь перевод целиком
    /// (иначе останется «половина» перевода и суммы разойдутся).
    /// </summary>
    public async Task<int?> CancelByTransactionAsync(int userId, int transactionId)
    {
        var transfer = await _db.Transfers
            .FirstOrDefaultAsync(t => t.UserId == userId
                && (t.FromTxId == transactionId || t.ToTxId == transactionId));
        if (transfer is null) return null;

        await CancelAsync(userId, transfer.Id);
        return transfer.Id;
    }

    /// <summary>Список переводов пользователя (по месяцу/году и/или счёту).</summary>
    public async Task<List<Transfer>> ListAsync(int userId, int? month = null, int? year = null, int? accountId = null)
    {
        var q = _db.Transfers
            .Include(t => t.FromAccount)
            .Include(t => t.ToAccount)
            .Where(t => t.UserId == userId);

        if (year is int y) q = q.Where(t => t.Date.Year == y);
        if (month is int m) q = q.Where(t => t.Date.Month == m);
        if (accountId is int aid)
            q = q.Where(t => t.FromAccountId == aid || t.ToAccountId == aid);

        return await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync();
    }

    /// <summary>Идентификаторы операций, входящих в переводы (для исключения из журнала операций).</summary>
    public async Task<HashSet<int>> TransferTransactionIdsAsync(int userId)
    {
        var ids = new HashSet<int>();
        var rows = await _db.Transfers
            .Where(t => t.UserId == userId && (t.FromTxId != null || t.ToTxId != null))
            .Select(t => new { t.FromTxId, t.ToTxId })
            .ToListAsync();
        foreach (var r in rows)
        {
            if (r.FromTxId is int f) ids.Add(f);
            if (r.ToTxId is int i) ids.Add(i);
        }
        return ids;
    }

    /// <summary>Пересчёт остатков указанных счетов (старт + операции + кэшбэк).</summary>
    private async Task RecalcAsync(int userId, params int[] accountIds)
    {
        foreach (var id in accountIds.Distinct())
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
            if (acc is null) continue;

            var signed = await _db.Transactions.Where(t => t.AccountId == id)
                .Select(t => new { t.Type, t.Amount, t.IsTransfer }).ToListAsync();
            var net = signed.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);

            if (acc.Type == AccountType.CreditCard)
            {
                acc.Balance = Math.Max(0, acc.StartingBalance - net);
                acc.CashbackAccrued = 0;
            }
            else
            {
                var cashback = acc.CashbackRate > 0
                    ? Math.Round(signed.Where(t => t.Type == TxType.Expense && !t.IsTransfer).Sum(t => t.Amount) * acc.CashbackRate / 100m, 2)
                    : 0m;
                acc.Balance = Math.Max(0, acc.StartingBalance + net + cashback);
                acc.CashbackAccrued = cashback;
            }
        }
        await _db.SaveChangesAsync();
    }

    private async Task<Category> EnsureCategoryAsync(int userId, TxType type)
    {
        var existing = await _db.Categories
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Name == CategoryName && c.Type == type);
        if (existing is not null) return existing;

        var cat = new Category
        {
            UserId = userId,
            Name = CategoryName,
            Type = type,
            Color = "#718096",
            IsSystem = true,
        };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        return cat;
    }
}
