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
/// Общий охват «я / семья» для долгов, обязательных платежей и календаря.
/// В режиме «семья» доступны и чужие записи, включая их изменение.
/// </summary>
public class FamilyScopeTests
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

    private static async Task<(User Me, User Spouse, User Stranger)> AddUsersAsync(AppDbContext db)
    {
        var me = new User { Email = "me@scope.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-S" };
        var spouse = new User { Email = "spouse@scope.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-S" };
        var stranger = new User { Email = "other@scope.ru", Name = "Чужой", PasswordHash = "x:y", FamilyCode = "FAM-X" };
        db.Users.AddRange(me, spouse, stranger);
        await db.SaveChangesAsync();
        return (me, spouse, stranger);
    }

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static T Controller<T>(AppDbContext db, T controller, int uid) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(uid) },
        };
        return controller;
    }

    private static void AddDebt(AppDbContext db, int uid, string name, decimal amount, DateOnly? due = null) =>
        db.Debts.Add(new Debt
        {
            UserId = uid, Direction = DebtDirection.IOwe, Counterparty = "Банк", Name = name,
            PrincipalAmount = amount, RemainingAmount = amount, Currency = "RUB",
            IssuedAt = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1), DueDate = due,
            Status = DebtStatus.Active,
        });

    // ---------- Разбор охвата ----------

    [Fact]
    public async Task ResolveScope_FamilyIncludesMembersAndDropsForeignIds()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, stranger) = await AddUsersAsync(db);

        var meScope = await FamilyAccess.ResolveScopeAsync(db, null, null, me.Id);
        var familyScope = await FamilyAccess.ResolveScopeAsync(db, "family", null, me.Id);
        var idsScope = await FamilyAccess.ResolveScopeAsync(
            db, "ids", $"{spouse.Id},{stranger.Id}", me.Id);

        Assert.Equal("me", meScope.Scope);
        Assert.Equal([me.Id], meScope.UserIds);
        Assert.Equal("family", familyScope.Scope);
        Assert.Equal(2, familyScope.UserIds.Count);
        Assert.Equal("ids", idsScope.Scope);
        Assert.Equal([spouse.Id], idsScope.UserIds);
    }

    // ---------- Долги ----------

    [Fact]
    public async Task Debts_FamilyScopeReadsAndManagesSpouseDebt()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, _) = await AddUsersAsync(db);
        AddDebt(db, me.Id, "Мой кредит", 1000m);
        AddDebt(db, spouse.Id, "Кредит супруга", 2000m);
        await db.SaveChangesAsync();
        var service = new DebtService(db);

        var mine = await service.ListAsync(me.Id, "all");
        var all = await service.ListAsync(me.Id, "all", [me.Id, spouse.Id]);
        var spouseDebt = await service.GetAsync(me.Id, all.First(d => d.UserId == spouse.Id).Id,
            [me.Id, spouse.Id]);

        Assert.Single(mine);
        Assert.Equal(2, all.Count);
        Assert.NotNull(spouseDebt);

        var allowed = await service.SetStatusAsync(me.Id, spouseDebt!.Id, true, [me.Id, spouse.Id]);
        Assert.True(allowed.Ok, allowed.Error);
        var denied = await service.SetStatusAsync(me.Id, spouseDebt.Id, false);
        Assert.False(denied.Ok);
    }

    [Fact]
    public async Task DebtsController_FamilyScopeReturnsBothSummaries()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, _) = await AddUsersAsync(db);
        AddDebt(db, me.Id, "Мой", 1000m);
        AddDebt(db, spouse.Id, "Супруга", 2000m);
        await db.SaveChangesAsync();
        var controller = Controller(db, new DebtsController(db, new DebtService(db)), me.Id);

        var meResult = Assert.IsType<OkObjectResult>(await controller.Summary(null, null));
        var familyResult = Assert.IsType<OkObjectResult>(await controller.Summary("family", null));
        var meDoc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(meResult.Value));
        var familyDoc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(familyResult.Value));

        Assert.Equal(1000m, meDoc.RootElement.GetProperty("iOwe").GetDecimal());
        Assert.Equal(3000m, familyDoc.RootElement.GetProperty("iOwe").GetDecimal());
        Assert.Equal(2, familyDoc.RootElement.GetProperty("activeCount").GetInt32());
    }

    // ---------- Платежи ----------

    [Fact]
    public async Task Obligations_FamilyScopeReadsAndPaysSpousePayment()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, _) = await AddUsersAsync(db);
        db.Accounts.Add(new Account
        {
            UserId = me.Id, Name = "Моя карта", Type = AccountType.DebitCard,
            StartingBalance = 1000m, Balance = 1000m, Color = "#fff",
        });
        db.Obligations.Add(new Obligation
        {
            UserId = spouse.Id, Name = "ЖКХ супруга", Payee = "УК",
            Amount = 300m, Periodicity = ObligationPeriodicity.Monthly,
            NextPaymentDate = DateOnly.FromDateTime(DateTime.Today).AddDays(2),
            Status = ObligationStatus.Active,
        });
        await db.SaveChangesAsync();
        var obligationId = (await db.Obligations.SingleAsync()).Id;
        var service = new ObligationService(db);

        var mine = await service.ListAsync(me.Id, "active");
        var all = await service.ListAsync(me.Id, "active", [me.Id, spouse.Id]);
        Assert.Empty(mine);
        Assert.Single(all);

        var result = await service.PayAsync(me.Id, obligationId, new ObligationService.PaymentInput(
            null, null, null, false, null, false), [me.Id, spouse.Id]);
        Assert.True(result.Ok, result.Error);
        Assert.Single(await db.ObligationPayments.ToListAsync());
    }

    // ---------- Календарь ----------

    [Fact]
    public async Task Calendar_FamilyScopeIncludesAllSourcesOfSpouse()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, _) = await AddUsersAsync(db);
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        db.CalendarEvents.Add(new CalendarEvent
        {
            UserId = spouse.Id, Title = "Событие супруга", Date = date, Type = CalendarEventType.Reminder,
        });
        db.Obligations.Add(new Obligation
        {
            UserId = spouse.Id, Name = "Платёж супруга", Payee = "УК", Amount = 300m,
            Periodicity = ObligationPeriodicity.Monthly, NextPaymentDate = date,
            Status = ObligationStatus.Active,
        });
        AddDebt(db, spouse.Id, "Долг супруга", 1000m, date);
        await db.SaveChangesAsync();
        var service = new CalendarService(db);

        var mine = await service.GetAsync(me.Id, date, date);
        var family = await service.GetAsync(me.Id, date, date, null, [me.Id, spouse.Id]);

        Assert.Empty(mine);
        Assert.Equal(3, family.Count);
        Assert.All(family, item =>
        {
            Assert.Equal(spouse.Id, item.OwnerId);
            Assert.Equal("Супруг", item.OwnerName);
        });
    }

    [Fact]
    public async Task CalendarController_FamilyScopeReturnsSpouseEvent()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse, _) = await AddUsersAsync(db);
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        db.CalendarEvents.Add(new CalendarEvent
        {
            UserId = spouse.Id, Title = "Событие супруга", Date = date, Type = CalendarEventType.Event,
        });
        await db.SaveChangesAsync();
        var controller = Controller(db, new CalendarController(db, new CalendarService(db)), me.Id);

        var result = Assert.IsType<OkObjectResult>(await controller.List(date, date, null, "family", null));
        var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }
}
