using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Переводы между своими и семейными счетами (Задача 7 + 0.3/N3).
///
/// Перевод — одна бизнес-сущность с двумя операциями:
///   расход на счёте-отправителе + доход на счёте-получателе, обе с IsTransfer = true,
///   поэтому перевод не искажает доходы/расходы и не попадает в аналитику.
///
/// Принадлежность (решение пользователя, 17.11.2026): перевод делается ОТ ВЛАДЕЛЬЦА
/// СЧЁТА-ОТПРАВИТЕЛЯ — запись Transfer и расходная половина принадлежат ему, доходная
/// половина — владельцу счёта-получателя. При этом перевод виден ВСЕМ членам семьи
/// (журнал переводов общий, с указанием владельцев счетов и кнопкой отмены).
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

        // Счёт должен быть доступен текущему пользователю: он сам или любой участник его семьи.
        var manageable = await FamilyAccess.ManageableUserIdsAsync(_db, userId);
        var from = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == fromAccountId && manageable.Contains(a.UserId));
        if (from is null) return new(false, "Счёт-отправитель не найден", null);
        if (from.IsArchived) return new(false, "Счёт-отправитель в архиве", null);

        var to = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == toAccountId && manageable.Contains(a.UserId));
        if (to is null) return new(false, "Счёт-получатель не найден", null);
        if (to.IsArchived) return new(false, "Счёт-получатель в архиве", null);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await RecalcAsync(from.Id, to.Id);
        var available = from.Type == AccountType.CreditCard
            ? Math.Max(0m, from.CreditLimit - from.Balance) : from.Balance;
        if (amount > available) return new(false, "Недостаточно средств на счёте-отправителе", null);
        if (to.Type == AccountType.CreditCard && amount > to.Balance)
            return new(false, "Сумма перевода превышает долг по кредитной карте", null);

        // Перевод принадлежит владельцу счёта-отправителя (N3): и запись Transfer,
        // и расходная половина. Доходная половина — владельцу счёта-получателя,
        // чтобы каждая операция была у хозяина своего счёта.
        var outCategory = await EnsureCategoryAsync(from.UserId, TxType.Expense);
        var inCategory = await EnsureCategoryAsync(to.UserId, TxType.Income);

        var transfer = new Transfer
        {
            UserId = from.UserId,
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = amount,
            Date = date,
            Comment = comment?.Trim() ?? "",
            CreatedAt = DateTime.UtcNow,
        };

        var outTx = new Transaction
        {
            UserId = from.UserId,
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
            UserId = to.UserId,
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

        await RecalcAsync(from.Id, to.Id);
        await transaction.CommitAsync();

        return new(true, null, transfer);
    }

    /// <summary>Отменяет перевод: удаляет обе операции и запись, пересчитывает остатки.</summary>
    /// Отменить может инициатор/владелец перевода или любой член семьи (N3).
    public async Task<(bool Ok, string? Error)> CancelAsync(int userId, int transferId)
    {
        var transfer = await _db.Transfers
            .Include(t => t.User)
            .Include(t => t.FromAccount).ThenInclude(a => a.User)
            .Include(t => t.ToAccount).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer is null) return (false, "Перевод не найден");
        if (!await CanManageTransferAsync(userId, transfer.UserId)) return (false, "Перевод не найден");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Половины принадлежат владельцам счетов (N3), поэтому ищем по id, а не по userId.
        if (transfer.FromTxId is int fromTxId)
        {
            var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == fromTxId);
            if (tx is not null) _db.Transactions.Remove(tx);
        }
        if (transfer.ToTxId is int toTxId)
        {
            var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == toTxId);
            if (tx is not null) _db.Transactions.Remove(tx);
        }
        _db.Transfers.Remove(transfer);
        await _db.SaveChangesAsync();

        await RecalcAsync(transfer.FromAccountId, transfer.ToAccountId);
        await transaction.CommitAsync();
        return (true, null);
    }

    /// <summary>
    /// Если операция входит в перевод — отменяем весь перевод целиком
    /// (иначе останется «половина» перевода и суммы разойдутся).
    /// Доступ: инициатор/владелец перевода или член семьи (N3).
    /// </summary>
    public async Task<int?> CancelByTransactionAsync(int userId, int transactionId)
    {
        var transfer = await _db.Transfers
            .FirstOrDefaultAsync(t => t.FromTxId == transactionId || t.ToTxId == transactionId);
        if (transfer is null) return null;
        if (!await CanManageTransferAsync(userId, transfer.UserId)) return null;

        await CancelAsync(userId, transfer.Id);
        return transfer.Id;
    }

    /// <summary>Инициатор/владелец перевода или любой член семьи может видеть и отменять перевод.</summary>
    private async Task<bool> CanManageTransferAsync(int userId, int transferUserId)
    {
        if (userId == transferUserId) return true;
        var manageable = await FamilyAccess.ManageableUserIdsAsync(_db, userId);
        return manageable.Contains(transferUserId);
    }

    /// <summary>
    /// Переводы, видимые пользователю: свои + переводы семьи (N3) — общий журнал семьи.
    /// Половины семейного перевода принадлежат владельцам счетов, поэтому ищем
    /// по идентификатору владельца перевода.
    /// </summary>
    public async Task<List<Transfer>> ListAsync(int userId, int? month = null, int? year = null, int? accountId = null)
    {
        var manageable = await FamilyAccess.ManageableUserIdsAsync(_db, userId);
        var q = _db.Transfers
            .Include(t => t.User)
            .Include(t => t.FromAccount).ThenInclude(a => a.User)
            .Include(t => t.ToAccount).ThenInclude(a => a.User)
            .Where(t => manageable.Contains(t.UserId));

        if (year is int y) q = q.Where(t => t.Date.Year == y);
        if (month is int m) q = q.Where(t => t.Date.Month == m);
        if (accountId is int aid)
            q = q.Where(t => t.FromAccountId == aid || t.ToAccountId == aid);

        return await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync();
    }

    /// <summary>Один перевод по идентификатору (с проверкой видимости семьи).</summary>
    public async Task<Transfer?> GetAsync(int userId, int transferId)
    {
        var transfer = await _db.Transfers
            .Include(t => t.User)
            .Include(t => t.FromAccount).ThenInclude(a => a.User)
            .Include(t => t.ToAccount).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer is null) return null;
        return await CanManageTransferAsync(userId, transfer.UserId) ? transfer : null;
    }

    /// <summary>
    /// Идентификаторы операций, входящих в переводы, ВИДИМЫЕ пользователю
    /// (для исключения из журнала операций): половины семейных переводов
    /// скрываются и у членов семьи (N3).
    /// </summary>
    public async Task<HashSet<int>> TransferTransactionIdsAsync(int userId)
    {
        var ids = new HashSet<int>();
        var manageable = await FamilyAccess.ManageableUserIdsAsync(_db, userId);
        var rows = await _db.Transfers
            .Where(t => manageable.Contains(t.UserId) && (t.FromTxId != null || t.ToTxId != null))
            .Select(t => new { t.FromTxId, t.ToTxId })
            .ToListAsync();
        foreach (var r in rows)
        {
            if (r.FromTxId is int f) ids.Add(f);
            if (r.ToTxId is int i) ids.Add(i);
        }
        return ids;
    }

    /// <summary>Пересчёт остатков указанных счетов (формула — в AccountBalanceService).</summary>
    private Task RecalcAsync(params int[] accountIds) =>
        AccountBalanceService.RecalcAsync(_db, accountIds);

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
