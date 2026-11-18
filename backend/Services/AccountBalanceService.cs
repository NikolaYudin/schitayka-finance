using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Единый расчёт остатков счетов: стартовый баланс + операции + начисленный кэшбэк.
///
/// Раньше эта формула была скопирована в четырёх местах (AccountsController,
/// TransactionsController, TransferService, AccountRulesController) и уже начала
/// расходиться. Теперь все вызывающие используют этот сервис.
/// </summary>
public static class AccountBalanceService
{
    /// <summary>Операция в компактном виде — чтобы не загружать сущности целиком.</summary>
    /// <param name="NoCashback">
    /// Операция не участвует в начислении кэшбэка (возврат долга — не покупка).
    /// </param>
    public readonly record struct BalanceTx(TxType Type, decimal Amount, bool IsTransfer, bool NoCashback = false);

    /// <summary>
    /// Категории, по которым кэшбэк не начисляется: возврат долга — движение денег,
    /// а не покупка (Задача 2, решение 18.11.2026). Иначе возврат 3000 ₽ уменьшал бы
    /// счёт только на 2850 ₽ при ставке кэшбэка 5%.
    /// </summary>
    private static readonly string[] NoCashbackCategories = { DebtService.CategoryName };

    /// <summary>Пересчитывает остатки счетов из переданного запроса и сохраняет изменения.</summary>
    public static async Task RecalcAsync(
        AppDbContext db, IQueryable<Account> accountsQuery, CancellationToken ct = default)
    {
        var ids = await accountsQuery.Select(a => a.Id).ToListAsync(ct);
        await RecalcAsync(db, ids, ct);
    }

    /// <summary>Пересчитывает остатки по списку id (один запрос операций на все счета).</summary>
    public static async Task RecalcAsync(
        AppDbContext db, IReadOnlyCollection<int> accountIds, CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return;
        var ids = accountIds.Distinct().ToList();

        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count == 0) return;

        var rows = await db.Transactions
            .Where(t => t.AccountId != null && ids.Contains(t.AccountId.Value))
            .Select(t => new
            {
                AccountId = t.AccountId!.Value, t.Type, t.Amount, t.IsTransfer,
                NoCashback = NoCashbackCategories.Contains(t.Category.Name),
            })
            .ToListAsync(ct);

        var byAccount = rows
            .GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BalanceTx>)g
                .Select(t => new BalanceTx(t.Type, t.Amount, t.IsTransfer, t.NoCashback)).ToList());

        foreach (var acc in accounts)
        {
            IReadOnlyList<BalanceTx> list = byAccount.TryGetValue(acc.Id, out var found)
                ? found : Array.Empty<BalanceTx>();
            var (balance, cashback) = Compute(acc, list);
            acc.Balance = balance;
            acc.CashbackAccrued = cashback;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Остаток и начисленный кэшбэк по одному счёту.
    /// Отрицательный остаток по дебету/наличным показываем как 0 — это историческое
    /// поведение интерфейса (перерасход не отображается минусом). Для кредитной карты
    /// Balance — это сумма долга.
    /// </summary>
    public static (decimal Balance, decimal Cashback) Compute(
        Account account, IReadOnlyCollection<BalanceTx> transactions)
    {
        var net = transactions.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);

        if (account.Type == AccountType.CreditCard)
            return (Math.Max(0, account.StartingBalance - net), 0m);

        var cashback = account.CashbackRate > 0
            ? Math.Round(
                transactions.Where(t => t.Type == TxType.Expense && !t.IsTransfer && !t.NoCashback)
                    .Sum(t => t.Amount) * account.CashbackRate / 100m,
                2)
            : 0m;

        return (Math.Max(0, account.StartingBalance + net + cashback), cashback);
    }
}
