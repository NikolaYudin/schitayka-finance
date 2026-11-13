using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Переводы между своими счетами (Задача 7).</summary>
public class TransferTests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db, User User)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new User { Email = "test@schitayka.ru", Name = "Тест", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (conn, db, user);
    }

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static T WithUser<T>(T controller, int userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    private static async Task<(Account From, Account To)> AddAccountsAsync(AppDbContext db, User user,
        decimal fromStart = 10000m, decimal toStart = 0m)
    {
        var from = new Account
        {
            UserId = user.Id, Name = "Карта", Type = AccountType.DebitCard,
            StartingBalance = fromStart, Balance = fromStart,
        };
        var to = new Account
        {
            UserId = user.Id, Name = "Копилка", Type = AccountType.Deposit,
            StartingBalance = toStart, Balance = toStart,
        };
        db.AddRange(from, to);
        await db.SaveChangesAsync();
        return (from, to);
    }

    [Fact]
    public async Task Create_MakesTwoOperationsAndMovesMoney()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);

        var result = await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, new DateOnly(2026, 11, 13), "На копилку");

        Assert.True(result.Ok);
        Assert.NotNull(result.Transfer);

        var txs = await db.Transactions.Where(t => t.UserId == user.Id).ToListAsync();
        Assert.Equal(2, txs.Count);
        Assert.All(txs, t => Assert.True(t.IsTransfer));
        Assert.Equal(3000m, txs.Single(t => t.Type == TxType.Expense).Amount);
        Assert.Equal(3000m, txs.Single(t => t.Type == TxType.Income).Amount);

        // Остатки: 10 000 − 3 000 и 0 + 3 000
        Assert.Equal(7000m, (await db.Accounts.FindAsync(from.Id))!.Balance);
        Assert.Equal(3000m, (await db.Accounts.FindAsync(to.Id))!.Balance);
    }

    [Fact]
    public async Task Create_RejectsSameAccountZeroAmountAndArchived()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);

        var same = await service.CreateAsync(user.Id, from.Id, from.Id, 100m, new DateOnly(2026, 11, 13), null);
        Assert.False(same.Ok);

        var zero = await service.CreateAsync(user.Id, from.Id, to.Id, 0m, new DateOnly(2026, 11, 13), null);
        Assert.False(zero.Ok);

        to.IsArchived = true;
        await db.SaveChangesAsync();
        var archived = await service.CreateAsync(user.Id, from.Id, to.Id, 100m, new DateOnly(2026, 11, 13), null);
        Assert.False(archived.Ok);
        Assert.Contains("архиве", archived.Error ?? "");
    }

    [Fact]
    public async Task Create_ForeignAccount_IsRejected()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, _) = await AddAccountsAsync(db, user);
        var otherUser = new User { Email = "other@schitayka.ru", Name = "Другой", PasswordHash = "x:y" };
        db.Users.Add(otherUser);
        await db.SaveChangesAsync();
        var other = new Account { UserId = otherUser.Id, Name = "Чужой", Type = AccountType.DebitCard };
        db.Accounts.Add(other);
        await db.SaveChangesAsync();

        var service = new TransferService(db);
        var result = await service.CreateAsync(user.Id, from.Id, other.Id, 100m, new DateOnly(2026, 11, 13), null);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Cancel_RemovesBothOperationsAndRestoresBalances()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);
        var created = await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, new DateOnly(2026, 11, 13), null);
        Assert.True(created.Ok);

        var (ok, error) = await service.CancelAsync(user.Id, created.Transfer!.Id);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(await db.Transactions.Where(t => t.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.Transfers.Where(t => t.UserId == user.Id).ToListAsync());
        Assert.Equal(10000m, (await db.Accounts.FindAsync(from.Id))!.Balance);
        Assert.Equal(0m, (await db.Accounts.FindAsync(to.Id))!.Balance);
    }

    [Fact]
    public async Task CancelByTransaction_CancelsWholeTransfer()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);
        var created = await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, new DateOnly(2026, 11, 13), null);
        Assert.True(created.Ok);

        var outTxId = created.Transfer!.FromTxId!.Value;
        var canceledTransferId = await service.CancelByTransactionAsync(user.Id, outTxId);

        Assert.Equal(created.Transfer.Id, canceledTransferId);
        Assert.Empty(await db.Transactions.Where(t => t.UserId == user.Id).ToListAsync());
        Assert.Equal(10000m, (await db.Accounts.FindAsync(from.Id))!.Balance);
        Assert.Equal(0m, (await db.Accounts.FindAsync(to.Id))!.Balance);
    }

    [Fact]
    public async Task List_ReturnsTransfersForMonthAndAccount()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);
        await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, new DateOnly(2026, 11, 13), null);
        await service.CreateAsync(user.Id, from.Id, to.Id, 500m, new DateOnly(2026, 10, 1), null);

        var november = await service.ListAsync(user.Id, month: 11, year: 2026);
        var december = await service.ListAsync(user.Id, month: 10, year: 2026);
        var byAccount = await service.ListAsync(user.Id, accountId: to.Id);

        Assert.Single(november);
        Assert.Single(december);
        Assert.Equal(2, byAccount.Count);
    }

    [Fact]
    public async Task Transfer_DoesNotEarnCashback_AndRejectsOverspending()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var connection = conn;
        using var context = db;
        var (from, to) = await AddAccountsAsync(db, user);
        from.CashbackRate = 5m;
        await db.SaveChangesAsync();
        var service = new TransferService(db);
        var result = await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, DateOnly.FromDateTime(DateTime.Today), null);
        Assert.True(result.Ok);
        await AccountsController.RecalcAccountsOf(db, db.Accounts.Where(a => a.UserId == user.Id));
        Assert.Equal(0m, from.CashbackAccrued);
        Assert.Equal(10000m, from.Balance + to.Balance);
        var rejected = await service.CreateAsync(user.Id, from.Id, to.Id, 8000m, DateOnly.FromDateTime(DateTime.Today), null);
        Assert.False(rejected.Ok);
        Assert.Single(await db.Transfers.ToListAsync());
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task TransferOperations_AreHiddenFromJournalButStayInAnalytics()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var (from, to) = await AddAccountsAsync(db, user);
        var service = new TransferService(db);
        await service.CreateAsync(user.Id, from.Id, to.Id, 3000m, new DateOnly(2026, 11, 13), null);

        // Журнал операций не показывает стороны перевода…
        var controller = WithUser(
            new TransactionsController(db, new AccountRulesService(db)), user.Id);
        var list = await controller.List(11, 2026);
        var journal = Assert.IsType<OkObjectResult>(list);
        var json = JsonSerializer.Serialize(journal.Value, journal.Value!.GetType());
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.EnumerateArray());

        // …а аналитика их тоже не считает: доход/расход остаются нулевыми.
        var budget = WithUser(new BudgetController(db, new AccountRulesService(db)), user.Id);
        var summary = await budget.Summary(2026, 11);
        var summaryOk = Assert.IsType<OkObjectResult>(summary);
        var summaryJson = JsonSerializer.Serialize(summaryOk.Value, summaryOk.Value!.GetType());
        using var sdoc = JsonDocument.Parse(summaryJson);
        Assert.Equal(0m, sdoc.RootElement.GetProperty("monthIncome").GetDecimal());
        Assert.Equal(0m, sdoc.RootElement.GetProperty("monthExpense").GetDecimal());
    }
}
