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
/// Задача 3: обязательные платежи — периодичность и даты, статусы,
/// факты оплаты с необязательной операцией по счёту и сводка для дашборда.
/// </summary>
public class ObligationTests
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
        AppDbContext db, decimal startingBalance = 5000m, decimal cashbackRate = 0m,
        string email = "bill@schitayka.ru")
    {
        var user = new User { Email = email, Name = "Плательщик", PasswordHash = "x:y" };
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

    private static ObligationService.ObligationInput Input(
        string? name = "Коммунальные услуги",
        string? payee = "УК ЖКХ",
        decimal amount = 1000m,
        int periodicity = (int)ObligationPeriodicity.Monthly,
        DateOnly? nextPaymentDate = null,
        int? paymentDay = null,
        int? intervalDays = null,
        int? accountId = null,
        int? categoryId = null,
        int? debtId = null,
        bool remind = false) =>
        new(name, payee, categoryId, amount, periodicity,
            nextPaymentDate, paymentDay, intervalDays, accountId, remind, debtId, "");

    private static ObligationService.PaymentInput Payment(
        decimal? amount = null, DateOnly? date = null, bool createTransaction = false,
        int? accountId = null, bool reduceDebt = false) =>
        new(amount, date, null, createTransaction, accountId, reduceDebt);

    private static async Task<Debt> AddDebtAsync(
        AppDbContext db, int userId, decimal principal = 10000m,
        DebtDirection direction = DebtDirection.IOwe)
    {
        var debt = new Debt
        {
            UserId = userId,
            Direction = direction,
            Counterparty = "Банк",
            Name = "Ипотека",
            PrincipalAmount = principal,
            RemainingAmount = principal,
            Currency = "RUB",
            IssuedAt = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1),
            Status = DebtStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        db.Debts.Add(debt);
        await db.SaveChangesAsync();
        return debt;
    }

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static ObligationsController ControllerFor(AppDbContext db, int userId)
    {
        var controller = new ObligationsController(db, new ObligationService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    // ---------- Чистые расчёты ----------

    [Fact]
    public void Advance_Monthly_PreservesPaymentDay()
    {
        var from = new DateOnly(2026, 1, 15);
        var next = ObligationCalculator.Advance(
            from, ObligationPeriodicity.Monthly, paymentDay: 20, intervalDays: null);
        Assert.Equal(new DateOnly(2026, 2, 20), next);
    }

    [Fact]
    public void Advance_Monthly_Clamps31ToLastDayOfMonth()
    {
        var from = new DateOnly(2026, 1, 31);
        var next = ObligationCalculator.Advance(
            from, ObligationPeriodicity.Monthly, paymentDay: 31, intervalDays: null);
        Assert.Equal(new DateOnly(2026, 2, 28), next);

        var following = ObligationCalculator.Advance(
            next, ObligationPeriodicity.Monthly, paymentDay: 31, intervalDays: null);
        Assert.Equal(new DateOnly(2026, 3, 31), following);
    }

    [Fact]
    public void Advance_AllPeriods()
    {
        var from = new DateOnly(2026, 5, 10);
        Assert.Equal(new DateOnly(2026, 8, 10),
            ObligationCalculator.Advance(from, ObligationPeriodicity.Quarterly, null, null));
        Assert.Equal(new DateOnly(2027, 5, 10),
            ObligationCalculator.Advance(from, ObligationPeriodicity.Yearly, null, null));
        Assert.Equal(new DateOnly(2026, 5, 25),
            ObligationCalculator.Advance(from, ObligationPeriodicity.Custom, null, 15));
        Assert.Equal(from,
            ObligationCalculator.Advance(from, ObligationPeriodicity.OneTime, null, null));
    }

    [Fact]
    public void IsOverdue_AndStatusName()
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.True(ObligationCalculator.IsOverdue(
            ObligationStatus.Active, today.AddDays(-1), today));
        Assert.False(ObligationCalculator.IsOverdue(
            ObligationStatus.Paused, today.AddDays(-1), today));
        Assert.False(ObligationCalculator.IsOverdue(
            ObligationStatus.Active, today.AddDays(1), today));

        Assert.Equal("Просрочен", ObligationCalculator.StatusName(
            ObligationStatus.Active, today.AddDays(-1), today));
        Assert.Equal("На паузе", ObligationCalculator.StatusName(
            ObligationStatus.Paused, today.AddDays(-1), today));
        Assert.Equal("Завершён", ObligationCalculator.StatusName(
            ObligationStatus.Completed, today.AddDays(-1), today));
        Assert.Equal("Активен", ObligationCalculator.StatusName(
            ObligationStatus.Active, today.AddDays(1), today));
        Assert.Equal("Ежеквартально",
            ObligationCalculator.PeriodicityName(ObligationPeriodicity.Quarterly));
    }

    // ---------- CRUD и валидация ----------

    [Fact]
    public async Task Create_ValidatesFields()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);

        Assert.Contains("Название", (await service.CreateAsync(user.Id, Input(name: ""))).Error);
        Assert.Contains("получателя", (await service.CreateAsync(user.Id, Input(payee: ""))).Error);
        Assert.Contains("больше нуля", (await service.CreateAsync(user.Id, Input(amount: 0))).Error);
        Assert.Contains("копеек", (await service.CreateAsync(user.Id, Input(amount: 1.005m))).Error);
        Assert.Contains("периодичность", (await service.CreateAsync(user.Id, Input(periodicity: 99))).Error);
        Assert.Contains("интервал", (await service.CreateAsync(user.Id, Input(
            periodicity: (int)ObligationPeriodicity.Custom))).Error);
        Assert.Contains("от 1 до 31", (await service.CreateAsync(user.Id, Input(paymentDay: 32))).Error);

        var (ok, error, obligation) = await service.CreateAsync(user.Id, Input(remind: true));
        Assert.True(ok, error);
        Assert.True(obligation!.Remind);
        Assert.Equal(ObligationStatus.Active, obligation.Status);
    }

    [Fact]
    public async Task Create_RejectsForeignCategoryAccountAndDebt()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var (other, _) = await AddUserWithAccountAsync(db, email: "other@schitayka.ru");
        var otherAccount = await db.Accounts.FirstAsync(a => a.UserId == other.Id);
        var otherCategory = new Category
        {
            UserId = other.Id, Name = "Чужая", Type = TxType.Expense, Color = "#fff",
        };
        db.Categories.Add(otherCategory);
        await db.SaveChangesAsync();
        var service = new ObligationService(db);

        Assert.Contains("Счёт", (await service.CreateAsync(user.Id, Input(accountId: otherAccount.Id))).Error);
        Assert.Contains("Категория", (await service.CreateAsync(user.Id, Input(categoryId: otherCategory.Id))).Error);
        Assert.Contains("Долг", (await service.CreateAsync(user.Id, Input(debtId: 999))).Error);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, active) = await service.CreateAsync(user.Id, Input());
        var (_, _, pause) = await service.CreateAsync(user.Id, Input(name: "Подписка", amount: 300m));
        var (_, _, done) = await service.CreateAsync(user.Id, Input(name: "Налог", amount: 700m));
        await service.SetStatusAsync(user.Id, pause!.Id, ObligationStatus.Paused);
        await service.SetStatusAsync(user.Id, done!.Id, ObligationStatus.Completed);

        Assert.Single(await service.ListAsync(user.Id, "active"));
        Assert.Equal(active!.Id, (await service.ListAsync(user.Id, "active"))[0].Id);
        Assert.Single(await service.ListAsync(user.Id, "paused"));
        Assert.Single(await service.ListAsync(user.Id, "completed"));
        Assert.Equal(3, (await service.ListAsync(user.Id, "all")).Count);
    }

    // ---------- Оплата ----------

    [Fact]
    public async Task Pay_RecordsFactAndAdvancesNextDate()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var planned = new DateOnly(2026, 10, 20);
        var (_, _, obligation) = await service.CreateAsync(
            user.Id, Input(nextPaymentDate: planned, paymentDay: 20));

        var (ok, error) = await service.PayAsync(user.Id, obligation!.Id, Payment());

        Assert.True(ok, error);
        var updated = await service.GetAsync(user.Id, obligation.Id);
        Assert.Single(updated!.Payments);
        Assert.Equal(planned, updated.Payments[0].PlannedDate);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), updated.Payments[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 20), updated.NextPaymentDate);
        Assert.Equal(ObligationStatus.Active, updated.Status);
        Assert.Empty(await db.Transactions.ToListAsync()); // без галочки операций нет
    }

    [Fact]
    public async Task Pay_WithTransaction_CreatesExpenseAndUpdatesBalance()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db, cashbackRate: 5m);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(accountId: account.Id));

        var (ok, error) = await service.PayAsync(user.Id, obligation!.Id,
            Payment(amount: 1000m, createTransaction: true));

        Assert.True(ok, error);
        var tx = Assert.Single(await db.Transactions.ToListAsync());
        Assert.Equal(TxType.Expense, tx.Type);
        Assert.Equal(1000m, tx.Amount);
        Assert.False(tx.IsTransfer);
        Assert.Contains("Обязательный платёж", tx.Comment);
        var category = await db.Categories.SingleAsync(c => c.Id == tx.CategoryId);
        Assert.Equal(ObligationService.FallbackCategoryName, category.Name);

        // В отличие от возврата долга, реальная трата даёт кэшбэк.
        var updatedAccount = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(4050m, updatedAccount.Balance);
        Assert.Equal(50m, updatedAccount.CashbackAccrued);
    }

    [Fact]
    public async Task Pay_WithTransaction_UsesOwnCategoryAndChosenAccount()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var otherAccount = new Account
        {
            UserId = user.Id, Name = "Резерв", Type = AccountType.Cash,
            StartingBalance = 2000m, Balance = 2000m, Color = "#000",
        };
        db.Accounts.Add(otherAccount);
        var category = new Category
        {
            UserId = user.Id, Name = "Коммуналка", Type = TxType.Expense, Color = "#f00",
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(
            user.Id, Input(categoryId: category.Id, accountId: account.Id));

        var (ok, error) = await service.PayAsync(user.Id, obligation!.Id,
            Payment(createTransaction: true, accountId: otherAccount.Id));

        Assert.True(ok, error);
        var tx = Assert.Single(await db.Transactions.ToListAsync());
        Assert.Equal(category.Id, tx.CategoryId);
        Assert.Equal(otherAccount.Id, tx.AccountId);
    }

    [Fact]
    public async Task Pay_OneTime_CompletesObligation()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(
            user.Id, Input(periodicity: (int)ObligationPeriodicity.OneTime));

        Assert.True((await service.PayAsync(user.Id, obligation!.Id, Payment())).Ok);

        var updated = await service.GetAsync(user.Id, obligation.Id);
        Assert.Equal(ObligationStatus.Completed, updated!.Status);
        Assert.Contains("завершён", (await service.PayAsync(user.Id, obligation.Id, Payment())).Error!);
    }

    [Fact]
    public async Task Pay_RejectsPausedFutureDateAndMissingAccount()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input());

        Assert.True((await service.SetStatusAsync(
            user.Id, obligation!.Id, ObligationStatus.Paused)).Ok);
        Assert.Contains("паузе", (await service.PayAsync(user.Id, obligation.Id, Payment())).Error!);

        await service.SetStatusAsync(user.Id, obligation.Id, ObligationStatus.Active);
        var tomorrow = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        Assert.Contains("будущем", (await service.PayAsync(user.Id, obligation.Id,
            Payment(date: tomorrow))).Error!);
        Assert.Contains("счёт", (await service.PayAsync(user.Id, obligation.Id,
            Payment(createTransaction: true))).Error!);
    }

    [Fact]
    public async Task DeletePayment_RestoresDateAndRemovesTransaction()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var planned = new DateOnly(2026, 11, 5);
        var (_, _, obligation) = await service.CreateAsync(
            user.Id, Input(nextPaymentDate: planned, paymentDay: 5, accountId: account.Id));
        await service.PayAsync(user.Id, obligation!.Id,
            Payment(createTransaction: true, accountId: account.Id));
        var payment = (await service.GetAsync(user.Id, obligation.Id))!.Payments[0];

        var (ok, error) = await service.DeletePaymentAsync(user.Id, obligation.Id, payment.Id);

        Assert.True(ok, error);
        var updated = await service.GetAsync(user.Id, obligation.Id);
        Assert.Empty(updated!.Payments);
        Assert.Equal(planned, updated.NextPaymentDate);
        Assert.Equal(5000m, (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id)).Balance);
        Assert.Empty(await db.Transactions.ToListAsync());
    }

    [Fact]
    public async Task SetStatus_PauseResumeComplete()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input());

        Assert.True((await service.SetStatusAsync(
            user.Id, obligation!.Id, ObligationStatus.Paused)).Ok);
        Assert.Equal(ObligationStatus.Paused, (await service.GetAsync(user.Id, obligation.Id))!.Status);
        Assert.True((await service.SetStatusAsync(
            user.Id, obligation.Id, ObligationStatus.Active)).Ok);
        Assert.True((await service.SetStatusAsync(
            user.Id, obligation.Id, ObligationStatus.Completed)).Ok);
        Assert.Equal(ObligationStatus.Completed, (await service.GetAsync(user.Id, obligation.Id))!.Status);
    }

    [Fact]
    public async Task Delete_Obligation_RemovesPaymentsAndTransactions()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(accountId: account.Id));
        await service.PayAsync(user.Id, obligation!.Id,
            Payment(createTransaction: true, accountId: account.Id));

        var (ok, error) = await service.DeleteAsync(user.Id, obligation.Id);

        Assert.True(ok, error);
        Assert.Null(await service.GetAsync(user.Id, obligation.Id));
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.ObligationPayments.ToListAsync());
        Assert.Equal(5000m, (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id)).Balance);
    }

    // ---------- Сводка и API ----------

    [Fact]
    public async Task Summary_CountsOverdueAndUpcoming()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await service.CreateAsync(user.Id, Input(amount: 1000m, nextPaymentDate: today.AddDays(2)));
        await service.CreateAsync(user.Id, Input(
            name: "Налог", amount: 500m, nextPaymentDate: today.AddDays(-3)));
        var (_, _, paused) = await service.CreateAsync(user.Id, Input(
            name: "Подписка", amount: 700m, nextPaymentDate: today.AddDays(-1)));
        await service.SetStatusAsync(user.Id, paused!.Id, ObligationStatus.Paused);

        var result = await ControllerFor(db, user.Id).Summary();
        var okResult = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(okResult.Value));
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("activeCount").GetInt32());
        Assert.Equal(1, root.GetProperty("pausedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("overdueCount").GetInt32());
        Assert.Equal(500m, root.GetProperty("overdueAmount").GetDecimal());
        Assert.Equal(1, root.GetProperty("dueIn7DaysCount").GetInt32());
        Assert.Equal(1000m, root.GetProperty("dueIn7DaysAmount").GetDecimal());
        Assert.Equal("Налог", root.GetProperty("next").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Controller_CreatePayAndDelete_ReturnObligationWithHistory()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var controller = ControllerFor(db, user.Id);

        var dto = new ObligationsController.ObligationDto(
            "Интернет", "Провайдер", null, 600m, (int)ObligationPeriodicity.Monthly,
            DateOnly.FromDateTime(DateTime.Today), 10, null, account.Id, true, null, "");
        var created = Assert.IsType<OkObjectResult>(await controller.Create(dto));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
        var id = doc.RootElement.GetProperty("Id").GetInt32();

        var paid = Assert.IsType<OkObjectResult>(await controller.Pay(id,
            new ObligationsController.PaymentDto(null, null, null, false, null)));
        using var paidDoc = JsonDocument.Parse(JsonSerializer.Serialize(paid.Value));
        Assert.Equal(1, paidDoc.RootElement.GetProperty("paymentsCount").GetInt32());
    }

    // ---------- Задача 4: связь с долгом ----------

    [Fact]
    public async Task Pay_ReduceDebt_CreatesRepaymentEventAndLowersRemaining()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id, principal: 6000m);
        var obligations = new ObligationService(db);
        var (_, _, obligation) = await obligations.CreateAsync(
            user.Id, Input(amount: 1000m, debtId: debt.Id, accountId: account.Id));

        var (ok, error) = await obligations.PayAsync(user.Id, obligation!.Id,
            Payment(createTransaction: true, accountId: account.Id, reduceDebt: true));

        Assert.True(ok, error);
        var updatedDebt = await db.Debts.AsNoTracking().SingleAsync(d => d.Id == debt.Id);
        Assert.Equal(5000m, updatedDebt.RemainingAmount);
        var ev = Assert.Single(await db.DebtEvents.ToListAsync());
        Assert.Equal(DebtEventKind.Repayment, ev.Kind);
        Assert.Equal(1000m, ev.Amount);
        Assert.Null(ev.TransactionId); // движение денег — только одна операция платежа
        var payment = Assert.Single(await db.ObligationPayments.ToListAsync());
        Assert.Equal(ev.Id, payment.DebtEventId);
        Assert.Single(await db.Transactions.ToListAsync());
    }

    [Fact]
    public async Task Pay_WithoutReduceDebt_LeavesDebtUnchanged()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(debtId: debt.Id));

        Assert.True((await service.PayAsync(user.Id, obligation!.Id, Payment())).Ok);

        Assert.Empty(await db.DebtEvents.ToListAsync());
        Assert.Equal(10000m, (await db.Debts.AsNoTracking().SingleAsync(d => d.Id == debt.Id)).RemainingAmount);
    }

    [Fact]
    public async Task ReduceDebt_RequiresLinkAndValidAmount()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new ObligationService(db);
        var (_, _, plain) = await service.CreateAsync(user.Id, Input());
        var debt = await AddDebtAsync(db, user.Id, principal: 500m);
        var (_, _, linked) = await service.CreateAsync(user.Id, Input(debtId: debt.Id, amount: 1000m));

        Assert.Contains("не связан", (await service.PayAsync(
            user.Id, plain!.Id, Payment(reduceDebt: true))).Error!);
        Assert.Contains("больше текущего остатка", (await service.PayAsync(
            user.Id, linked!.Id, Payment(reduceDebt: true))).Error!);
    }

    [Fact]
    public async Task LinkToOwedToMeDebt_IsRejected()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id, direction: DebtDirection.OwedToMe);
        var service = new ObligationService(db);

        var result = await service.CreateAsync(user.Id, Input(debtId: debt.Id));

        Assert.False(result.Ok);
        Assert.Contains("я должен", result.Error!);
    }

    [Fact]
    public async Task DeletePayment_ReduceDebt_RestoresDebtRemaining()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(debtId: debt.Id));
        await service.PayAsync(user.Id, obligation!.Id, Payment(reduceDebt: true));
        var payment = Assert.Single(await db.ObligationPayments.ToListAsync());

        var (ok, error) = await service.DeletePaymentAsync(user.Id, obligation.Id, payment.Id);

        Assert.True(ok, error);
        Assert.Empty(await db.DebtEvents.ToListAsync());
        Assert.Equal(10000m, (await db.Debts.AsNoTracking().SingleAsync(d => d.Id == debt.Id)).RemainingAmount);
    }

    [Fact]
    public async Task DeleteObligation_RemovesDebtEventsAndRestoresRemaining()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(debtId: debt.Id));
        await service.PayAsync(user.Id, obligation!.Id, Payment(reduceDebt: true));

        var (ok, error) = await service.DeleteAsync(user.Id, obligation.Id);

        Assert.True(ok, error);
        Assert.Empty(await db.DebtEvents.ToListAsync());
        Assert.Equal(10000m, (await db.Debts.AsNoTracking().SingleAsync(d => d.Id == debt.Id)).RemainingAmount);
    }

    [Fact]
    public async Task DebtEvent_FromPayment_CannotBeDeletedFromDebtHistory()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id);
        var obligations = new ObligationService(db);
        var (_, _, obligation) = await obligations.CreateAsync(user.Id, Input(debtId: debt.Id));
        await obligations.PayAsync(user.Id, obligation!.Id, Payment(reduceDebt: true));
        var ev = Assert.Single(await db.DebtEvents.ToListAsync());
        var debts = new DebtService(db);

        var (ok, error) = await debts.DeleteEventAsync(user.Id, debt.Id, ev.Id);

        Assert.False(ok);
        Assert.Contains("факт оплаты", error!);
    }

    [Fact]
    public async Task DebtCard_ShowsLinkedUpcomingObligations()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var debt = await AddDebtAsync(db, user.Id);
        var service = new ObligationService(db);
        var (_, _, obligation) = await service.CreateAsync(user.Id, Input(
            name: "Ипотечный платёж", amount: 700m, debtId: debt.Id,
            nextPaymentDate: DateOnly.FromDateTime(DateTime.Today).AddDays(5)));
        var controller = new DebtsController(db, new DebtService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(user.Id) },
        };

        var result = Assert.IsType<OkObjectResult>(await controller.Get(debt.Id));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var linked = doc.RootElement.GetProperty("linkedObligations");

        Assert.Equal(1, linked.GetArrayLength());
        Assert.Equal("Ипотечный платёж", linked[0].GetProperty("Name").GetString());
        Assert.Equal(700m, linked[0].GetProperty("Amount").GetDecimal());
        Assert.NotNull(obligation);
    }
}
