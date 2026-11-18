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

/// <summary>
/// Задача 2: долги — расчёт остатка и статуса, история событий,
/// необязательная операция по счёту, сводка для дашборда.
/// </summary>
public class DebtTests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private static async Task<(User User, Account Account)> AddUserWithAccountAsync(
        AppDbContext db, decimal startingBalance = 5000m, decimal cashbackRate = 0m)
    {
        var user = new User { Email = "debt@schitayka.ru", Name = "Должник", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var account = new Account
        {
            UserId = user.Id,
            Name = "Основная карта",
            Type = AccountType.DebitCard,
            StartingBalance = startingBalance,
            Balance = startingBalance,
            CashbackRate = cashbackRate,
            Color = "#007aff",
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (user, account);
    }

    private static DebtService.DebtInput Input(
        int direction = (int)DebtDirection.IOwe,
        decimal principal = 1000m,
        DateOnly? issuedAt = null,
        DateOnly? dueDate = null,
        int? accountId = null) =>
        new(direction, "Иван", "Займ на ремонт", principal, "RUB",
            issuedAt, dueDate, 0m, null, accountId, "");

    private static DebtService.DebtEventInput Event(
        DebtEventKind kind, decimal amount, DateOnly? date = null,
        bool createTransaction = false, int? accountId = null) =>
        new((int)kind, amount, date, null, createTransaction, accountId);

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static DebtsController ControllerFor(AppDbContext db, int userId)
    {
        var controller = new DebtsController(db, new DebtService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    // ---------- Чистые расчёты ----------

    [Fact]
    public void CalculateRemaining_AppliesAllEventKinds()
    {
        var events = new (DebtEventKind, decimal)[]
        {
            (DebtEventKind.Issued, 1000m),
            (DebtEventKind.Repayment, 200m),
            (DebtEventKind.InterestAccrued, 100m),
            (DebtEventKind.InterestPaid, 50m),
            (DebtEventKind.WriteOff, 50m),
            (DebtEventKind.Adjustment, -100m),
            (DebtEventKind.Adjustment, 30m),
        };

        Assert.Equal(730m, DebtCalculator.CalculateRemaining(1000m, events));
    }

    [Fact]
    public void CalculateRemaining_NeverGoesBelowZero()
    {
        var events = new (DebtEventKind, decimal)[] { (DebtEventKind.Repayment, 2500m) };
        Assert.Equal(0m, DebtCalculator.CalculateRemaining(1000m, events));
    }

    [Fact]
    public void Status_ReportsOverdueOnlyForActiveDebtsWithDueDate()
    {
        var today = new DateOnly(2026, 11, 18);
        var yesterday = today.AddDays(-1);

        Assert.Equal("Просрочен", DebtCalculator.StatusName(DebtStatus.Active, 500m, yesterday, today));
        Assert.Equal("Активен", DebtCalculator.StatusName(DebtStatus.Active, 500m, today.AddDays(5), today));
        Assert.Equal("Активен", DebtCalculator.StatusName(DebtStatus.Active, 500m, null, today));
        Assert.Equal("Закрыт", DebtCalculator.StatusName(DebtStatus.Closed, 500m, yesterday, today));
        Assert.Equal("Закрыт", DebtCalculator.StatusName(DebtStatus.Active, 0m, yesterday, today));
    }

    // ---------- Сервис ----------

    [Fact]
    public async Task Create_AddsIssuedEventAndFullRemaining()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);

        var (ok, error, debt) = await service.CreateAsync(user.Id, Input());

        Assert.True(ok, error);
        Assert.NotNull(debt);
        Assert.Equal(1000m, debt!.RemainingAmount);
        Assert.Equal(DebtStatus.Active, debt.Status);
        var issued = Assert.Single(db.DebtEvents.Where(e => e.DebtId == debt.Id));
        Assert.Equal(DebtEventKind.Issued, issued.Kind);
    }

    [Fact]
    public async Task Repayment_ReducesRemainingAndClosesWhenPaidOff()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input());

        var (ok, error) = await service.AddEventAsync(user.Id, debt!.Id, Event(DebtEventKind.Repayment, 400m));
        Assert.True(ok, error);

        var afterPartial = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(600m, afterPartial!.RemainingAmount);
        Assert.Equal(DebtStatus.Active, afterPartial.Status);

        (ok, error) = await service.AddEventAsync(user.Id, debt.Id, Event(DebtEventKind.Repayment, 600m));
        Assert.True(ok, error);

        var afterFull = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(0m, afterFull!.RemainingAmount);
        Assert.Equal(DebtStatus.Closed, afterFull.Status);
        Assert.Equal("Закрыт", DebtCalculator.StatusName(
            afterFull.Status, afterFull.RemainingAmount, afterFull.DueDate, DateOnly.FromDateTime(DateTime.Today)));
    }

    [Fact]
    public async Task Repayment_WithTransaction_CreatesExpenseAndRebalancesAccount()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input(accountId: account.Id));

        var (ok, error) = await service.AddEventAsync(
            user.Id, debt!.Id,
            Event(DebtEventKind.Repayment, 300m, createTransaction: true, accountId: account.Id));
        Assert.True(ok, error);

        var tx = await db.Transactions.SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(TxType.Expense, tx.Type);
        Assert.Equal(300m, tx.Amount);
        Assert.Equal(account.Id, tx.AccountId);
        Assert.Contains("Возврат долга", tx.Comment);

        var updatedAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(4700m, updatedAccount.Balance);

        var updatedDebt = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(700m, updatedDebt!.RemainingAmount);
        Assert.NotNull(updatedDebt.Events.Single(e => e.Kind == DebtEventKind.Repayment).TransactionId);
    }

    [Fact]
    public async Task Repayment_WithTransaction_DoesNotAccrueCashback()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db, startingBalance: 10000m, cashbackRate: 5m);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input(principal: 5000m, accountId: account.Id));

        var (ok, error) = await service.AddEventAsync(
            user.Id, debt!.Id,
            Event(DebtEventKind.Repayment, 3000m, createTransaction: true, accountId: account.Id));
        Assert.True(ok, error);

        // Возврат долга — не покупка: списывается ровно сумма возврата, кэшбэк не начисляется.
        var updatedAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(7000m, updatedAccount.Balance);
        Assert.Equal(0m, updatedAccount.CashbackAccrued);
    }

    [Fact]
    public async Task ReturnToMe_WithTransaction_CreatesIncome()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(
            user.Id, Input(direction: (int)DebtDirection.OwedToMe, accountId: account.Id));

        var (ok, error) = await service.AddEventAsync(
            user.Id, debt!.Id,
            Event(DebtEventKind.Repayment, 200m, createTransaction: true, accountId: account.Id));

        Assert.True(ok, error);
        var tx = await db.Transactions.SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(TxType.Income, tx.Type);
        var updatedAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(5200m, updatedAccount.Balance);
    }

    [Fact]
    public async Task DeleteEvent_RemovesLinkedTransactionAndReopensDebt()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input(accountId: account.Id));

        await service.AddEventAsync(
            user.Id, debt!.Id,
            Event(DebtEventKind.Repayment, 1000m, createTransaction: true, accountId: account.Id));

        var closed = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(DebtStatus.Closed, closed!.Status);
        var repaymentEvent = closed.Events.Single(e => e.Kind == DebtEventKind.Repayment);

        var (ok, error) = await service.DeleteEventAsync(user.Id, debt.Id, repaymentEvent.Id);

        Assert.True(ok, error);
        Assert.Empty(await db.Transactions.Where(t => t.UserId == user.Id).ToListAsync());
        var reopened = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(1000m, reopened!.RemainingAmount);
        Assert.Equal(DebtStatus.Active, reopened.Status);
        var updatedAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(5000m, updatedAccount.Balance);
    }

    [Fact]
    public async Task Repayment_MoreThanRemaining_IsRejected()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input());

        var (ok, error) = await service.AddEventAsync(
            user.Id, debt!.Id, Event(DebtEventKind.Repayment, 1500m));

        Assert.False(ok);
        Assert.Contains("больше текущего остатка", error);
    }

    [Fact]
    public async Task Update_Principal_RecalculatesRemaining()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input());
        await service.AddEventAsync(user.Id, debt!.Id, Event(DebtEventKind.Repayment, 200m));

        var (ok, error) = await service.UpdateAsync(
            user.Id, debt.Id, Input(principal: 1500m));

        Assert.True(ok, error);
        var updated = await service.GetAsync(user.Id, debt.Id);
        Assert.Equal(1300m, updated!.RemainingAmount);
    }

    [Fact]
    public async Task ManualClose_And_Reopen()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(user.Id, Input());

        Assert.True((await service.SetStatusAsync(user.Id, debt!.Id, close: true)).Ok);
        Assert.Equal(DebtStatus.Closed, (await service.GetAsync(user.Id, debt.Id))!.Status);

        Assert.True((await service.SetStatusAsync(user.Id, debt.Id, close: false)).Ok);
        Assert.Equal(DebtStatus.Active, (await service.GetAsync(user.Id, debt.Id))!.Status);
    }

    [Fact]
    public async Task Summary_SplitsDirectionsAndCountsOverdue()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);

        var today = DateOnly.FromDateTime(DateTime.Today);
        await service.CreateAsync(user.Id, Input(principal: 1000m));
        await service.CreateAsync(user.Id, Input(direction: (int)DebtDirection.OwedToMe, principal: 400m));
        var overdue = await service.CreateAsync(
            user.Id, Input(principal: 300m, issuedAt: today.AddDays(-30), dueDate: today.AddDays(-2)));

        var controller = ControllerFor(db, user.Id);
        var result = await controller.Summary();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1300m, root.GetProperty("iOwe").GetDecimal());
        Assert.Equal(400m, root.GetProperty("owedToMe").GetDecimal());
        Assert.Equal(-900m, root.GetProperty("net").GetDecimal());
        Assert.Equal(3, root.GetProperty("activeCount").GetInt32());
        Assert.Equal(1, root.GetProperty("overdueCount").GetInt32());
        Assert.Equal(300m, root.GetProperty("overdueAmount").GetDecimal());
        Assert.NotNull(overdue.Debt);
    }

    [Fact]
    public async Task List_FiltersActiveAndClosed()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new DebtService(db);

        var (_, _, active) = await service.CreateAsync(user.Id, Input());
        var (_, _, toClose) = await service.CreateAsync(user.Id, Input(principal: 500m));
        await service.AddEventAsync(user.Id, toClose!.Id, Event(DebtEventKind.Repayment, 500m));

        var activeList = await service.ListAsync(user.Id, "active");
        var closedList = await service.ListAsync(user.Id, "closed");
        var all = await service.ListAsync(user.Id, "all");

        Assert.Single(activeList);
        Assert.Equal(active!.Id, activeList[0].Id);
        Assert.Single(closedList);
        Assert.Equal(toClose.Id, closedList[0].Id);
        Assert.Equal(2, all.Count);
    }
}
