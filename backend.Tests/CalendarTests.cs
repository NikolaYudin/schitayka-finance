using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>
/// Задача 5: календарь событий — пользовательские события, объединение источников,
/// фильтры, диапазоны и ближайшие события для виджета.
/// </summary>
public class CalendarTests
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
        AppDbContext db, string email = "calendar@schitayka.ru")
    {
        var user = new User { Email = email, Name = "Владелец календаря", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var account = new Account
        {
            UserId = user.Id, Name = "Основная карта", Type = AccountType.DebitCard,
            StartingBalance = 5000m, Balance = 5000m, Color = "#007aff",
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (user, account);
    }

    private static CalendarService.CalendarEventInput EventInput(
        string? title = "Отдать документы", DateOnly? date = null, string? time = "14:30",
        int type = (int)CalendarEventType.Event, decimal? amount = null, bool remind = true,
        int? debtId = null, int? obligationId = null, int? accountId = null) =>
        new(title, date, time, type, amount, remind, "Заметка", debtId, obligationId, accountId);

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static CalendarController ControllerFor(AppDbContext db, int userId)
    {
        var controller = new CalendarController(db, new CalendarService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    private static async Task AddDebtAsync(AppDbContext db, int userId, DateOnly? dueDate)
    {
        db.Debts.Add(new Debt
        {
            UserId = userId, Direction = DebtDirection.IOwe, Counterparty = "Банк",
            Name = "Кредит", PrincipalAmount = 1000m, RemainingAmount = 1000m,
            Currency = "RUB", IssuedAt = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1),
            DueDate = dueDate, Status = DebtStatus.Active,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddObligationAsync(AppDbContext db, int userId, DateOnly date)
    {
        db.Obligations.Add(new Obligation
        {
            UserId = userId, Name = "ЖКХ", Payee = "УК", Amount = 800m,
            Periodicity = ObligationPeriodicity.Monthly, NextPaymentDate = date,
            Status = ObligationStatus.Active,
        });
        await db.SaveChangesAsync();
    }

    // ---------- Пользовательские события ----------

    [Fact]
    public async Task Create_ValidatesFields()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);

        var date = DateOnly.FromDateTime(DateTime.Today);
        Assert.Contains("Название", (await service.CreateAsync(user.Id, EventInput(title: "", date: date))).Error);
        Assert.Contains("дату", (await service.CreateAsync(user.Id, EventInput(date: null))).Error);
        Assert.Contains("Время", (await service.CreateAsync(user.Id, EventInput(date: date, time: "99:99"))).Error);
        Assert.Contains("тип", (await service.CreateAsync(user.Id, EventInput(date: date, type: 99))).Error);
        Assert.Contains("сумма", (await service.CreateAsync(user.Id, EventInput(date: date, amount: -5m))).Error);
        Assert.Contains("копеек", (await service.CreateAsync(user.Id, EventInput(date: date, amount: 1.005m))).Error);
    }

    [Fact]
    public async Task CreateUpdateDelete_UserEvent()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);
        var date = new DateOnly(2026, 11, 25);

        var (ok, error, created) = await service.CreateAsync(user.Id, EventInput(date: date, time: "9:05"));
        Assert.True(ok, error);
        Assert.Equal("09:05", created!.Time);
        Assert.Equal(CalendarEventType.Event, created.Type);

        var (updated, updateError) = await service.UpdateAsync(user.Id, created.Id,
            EventInput(title: "Новое название", date: date, time: "18:00",
                type: (int)CalendarEventType.Reminder, amount: 500m));
        Assert.True(updated, updateError);
        var fetched = await service.GetEventAsync(user.Id, created.Id);
        Assert.Equal("Новое название", fetched!.Title);
        Assert.Equal("18:00", fetched.Time);
        Assert.Equal(CalendarEventType.Reminder, fetched.Type);
        Assert.Equal(500m, fetched.Amount);

        Assert.True((await service.DeleteAsync(user.Id, created.Id)).Ok);
        Assert.Null(await service.GetEventAsync(user.Id, created.Id));
    }

    [Fact]
    public async Task Links_AreValidatedAndStored()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var (other, _) = await AddUserWithAccountAsync(db, email: "other@schitayka.ru");
        var service = new CalendarService(db);
        var date = DateOnly.FromDateTime(DateTime.Today);

        var (_, _, debt) = await new DebtService(db).CreateAsync(user.Id, new DebtService.DebtInput(
            (int)DebtDirection.IOwe, "Банк", "Кредит", 1000m, "RUB", date, null, 0m, null, null, ""));
        Assert.Contains("Счёт", (await service.CreateAsync(user.Id, EventInput(
            date: date, accountId: 999))).Error);

        var (ok, error, created) = await service.CreateAsync(user.Id, EventInput(
            date: date, accountId: account.Id, debtId: debt!.Id));
        Assert.True(ok, error);
        Assert.Equal(account.Id, created!.AccountId);
        Assert.Equal(debt.Id, created.DebtId);
        Assert.Contains("Платёж", (await service.CreateAsync(user.Id, EventInput(
            date: date, obligationId: 999))).Error);
        Assert.NotNull(other);
    }

    // ---------- Объединение источников ----------

    [Fact]
    public async Task Range_IncludesEverySource()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, account) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);
        var date = new DateOnly(2026, 11, 25);
        account.MaturesAt = date.ToDateTime(new TimeOnly(0, 0));
        await db.SaveChangesAsync();

        await service.CreateAsync(user.Id, EventInput(date: date));
        await AddObligationAsync(db, user.Id, date);
        await AddDebtAsync(db, user.Id, date);
        db.InterestAccruals.Add(new InterestAccrual
        {
            UserId = user.Id, AccountId = account.Id,
            From = date.AddDays(-30).ToDateTime(TimeOnly.MinValue),
            To = date.ToDateTime(TimeOnly.MinValue),
            Days = 30, Gross = 100m, Tax = 13m, Net = 87m, IsPaid = false,
        });
        await db.SaveChangesAsync();

        var items = await service.GetAsync(user.Id, date, date);

        Assert.Equal(5, items.Count);
        Assert.Contains(items, i => i.Source == (int)CalendarSource.UserEvent);
        Assert.Contains(items, i => i.Source == (int)CalendarSource.Obligation);
        Assert.Contains(items, i => i.Source == (int)CalendarSource.DebtDue);
        Assert.Contains(items, i => i.Source == (int)CalendarSource.DepositMaturity);
        Assert.Contains(items, i => i.Source == (int)CalendarSource.InterestAccrual);
        Assert.Contains(items, i => i.Title.Contains("ЖКХ"));
        Assert.Contains(items, i => i.Amount == 87m);
    }

    [Fact]
    public async Task Filter_BySourceTypes()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);
        var date = new DateOnly(2026, 11, 25);
        await service.CreateAsync(user.Id, EventInput(date: date));
        await AddObligationAsync(db, user.Id, date);
        await AddDebtAsync(db, user.Id, date);

        var onlyEvents = await service.GetAsync(user.Id, date, date, new[] { (int)CalendarSource.UserEvent });
        var eventsAndDebts = await service.GetAsync(user.Id, date, date,
            new[] { (int)CalendarSource.UserEvent, (int)CalendarSource.DebtDue });

        Assert.Single(onlyEvents);
        Assert.Equal(2, eventsAndDebts.Count);
    }

    [Fact]
    public async Task Upcoming_OrderedLimitedAndOverdueMarked()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);
        var today = DateOnly.FromDateTime(DateTime.Today);

        await service.CreateAsync(user.Id, EventInput(title: "Запланированное", date: today.AddDays(2)));
        await service.CreateAsync(user.Id, EventInput(title: "Запланированное", date: today.AddDays(1)));
        await service.CreateAsync(user.Id, EventInput(title: "Прошлое", date: today.AddDays(-1)));
        await service.CreateAsync(user.Id, EventInput(title: "Запланированное", date: today.AddDays(3)));
        await AddDebtAsync(db, user.Id, today.AddDays(-2));

        var items = await service.UpcomingAsync(user.Id, take: 3, days: 7);

        Assert.Equal(3, items.Count);
        Assert.Equal(today.AddDays(-2), items[0].Date);
        Assert.True(items[0].IsOverdue);
        Assert.Equal(today.AddDays(-1), items[1].Date);
        Assert.True(items[1].IsOverdue);
    }

    [Fact]
    public async Task Range_WorksWithSwappedDatesAndIsUserScoped()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var (other, _) = await AddUserWithAccountAsync(db, email: "other@schitayka.ru");
        var service = new CalendarService(db);
        var date = new DateOnly(2026, 11, 25);
        await service.CreateAsync(user.Id, EventInput(date: date));
        await service.CreateAsync(other.Id, EventInput(title: "Чужое", date: date));

        var swapped = await service.GetAsync(user.Id, date.AddDays(5), date.AddDays(-5));
        var mine = await service.GetAsync(user.Id, date, date);
        var foreign = await service.GetAsync(other.Id, date, date);

        Assert.Single(swapped);
        Assert.Single(mine);
        Assert.Single(foreign);
        Assert.DoesNotContain(mine, i => i.Title == "Чужое");
    }

    [Fact]
    public async Task Controller_ReturnsRangeAndUpcoming()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (user, _) = await AddUserWithAccountAsync(db);
        var service = new CalendarService(db);
        var today = DateOnly.FromDateTime(DateTime.Today);
        await service.CreateAsync(user.Id, EventInput(date: today.AddDays(1), amount: 250m));
        var controller = ControllerFor(db, user.Id);

        var list = Assert.IsType<OkObjectResult>(await controller.List(today, today.AddDays(2), null));
        var upcoming = Assert.IsType<OkObjectResult>(await controller.Upcoming(5, 7));

        Assert.NotNull(list.Value);
        Assert.NotNull(upcoming.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(list.Value));
        var items = doc.RootElement;
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Отдать документы", items[0].GetProperty("Title").GetString());
        Assert.Equal(250m, items[0].GetProperty("Amount").GetDecimal());
    }
}