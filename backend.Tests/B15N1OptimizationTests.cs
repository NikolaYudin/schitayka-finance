using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

// B15: отсутствие N+1 доказываем перехватом SQL — число команд не должно расти
// при увеличении числа счетов. Заодно фиксируем JSON-контракт проекций.
// Счётчик всех команд, которые EF отправляет в SQLite (в т.ч. на запись).
internal sealed class B15SqlCounter : DbCommandInterceptor
{
    public int Commands;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref Commands);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Commands);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Interlocked.Increment(ref Commands);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Commands);
        return ValueTask.FromResult(result);
    }
}

public class B15N1OptimizationTests
{
    private sealed record FamilyFixture(
        SqliteConnection Conn, AppDbContext Db, B15SqlCounter Counter, User Me, User Spouse);

    private static async Task<FamilyFixture> CreateFamilyDbAsync(int accountsPerUser)
    {
        var counter = new B15SqlCounter();
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .AddInterceptors(counter)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var me = new User { Email = "me@b15.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-B15" };
        var spouse = new User { Email = "sp@b15.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-B15" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();

        var bank = new Bank
        {
            Name = "Т-Банк", Code = "tbank", ShortName = "Т", BrandColor = "#FFDD2D", UserId = null,
        };
        db.Banks.Add(bank);
        await db.SaveChangesAsync();

        for (var i = 1; i <= accountsPerUser; i++)
        {
            db.Accounts.AddRange(
                new Account
                {
                    UserId = me.Id, Name = $"Мой счёт {i}", Type = AccountType.DebitCard,
                    StartingBalance = 1000 * i, Balance = 1000 * i, BankId = bank.Id,
                },
                new Account
                {
                    UserId = spouse.Id, Name = $"Счёт супруга {i}", Type = AccountType.DebitCard,
                    StartingBalance = 500 * i, Balance = 500 * i, BankId = bank.Id,
                });
        }
        await db.SaveChangesAsync();

        counter.Commands = 0; // подготовительные команды в счёт не идут
        return new FamilyFixture(conn, db, counter, me, spouse);
    }

    private static BudgetSummaryService SummaryService(AppDbContext db) =>
        new(db, new AccountRulesService(db));

    [Fact]
    public async Task B15_BudgetSummary_QueryCountIsConstant_AndBankOwnerFieldsSurvive()
    {
        var small = await CreateFamilyDbAsync(1);
        using var c1 = small.Conn;
        await using var d1 = small.Db;
        var smallResult = await SummaryService(d1).GetAsync(small.Me.Id, 2026, 11, "family", null);
        var smallCommands = small.Counter.Commands;

        var large = await CreateFamilyDbAsync(8);
        using var c2 = large.Conn;
        await using var d2 = large.Db;
        var largeResult = await SummaryService(d2).GetAsync(large.Me.Id, 2026, 11, "family", null);
        var largeCommands = large.Counter.Commands;

        // Число запросов не зависит от числа счетов (раньше было +2 на каждый свой счёт).
        Assert.Equal(smallCommands, largeCommands);
        Assert.Single(smallResult.Accounts);
        Assert.Equal(8, largeResult.Accounts.Count);
        Assert.Single(smallResult.FamilyAccounts);
        Assert.Equal(8, largeResult.FamilyAccounts.Count);

        // Значения, которые раньше давали коррелированные подзапросы, остались на месте.
        Assert.All(largeResult.Accounts, a =>
        {
            Assert.Equal("Т", a.BankShortName);
            Assert.Equal("#FFDD2D", a.BankColor);
        });
        Assert.All(largeResult.FamilyAccounts, a =>
        {
            Assert.Equal(large.Spouse.Id, a.OwnerId);
            Assert.Equal("Супруг", a.OwnerName);
            Assert.Equal("Т", a.BankShortName);
            Assert.Equal("#FFDD2D", a.BankColor);
        });

        // Контракт api.ts: AccountSummaryRow / FamilyAccountRow — без потери полей.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(largeResult));
        AssertPropertyNames(doc.RootElement.GetProperty("accounts")[0], "id", "name", "type", "typeName",
            "balance", "creditLimit", "cashbackRate", "cashbackAccrued", "color", "bankShortName", "bankColor");
        AssertPropertyNames(doc.RootElement.GetProperty("familyAccounts")[0], "id", "name", "type", "typeName",
            "balance", "creditLimit", "cashbackRate", "cashbackAccrued", "color", "ownerId", "ownerName",
            "bankShortName", "bankColor");
    }

    private static async Task<JsonElement> ListJsonAsync(AppDbContext db, int uid)
    {
        var controller = new AccountsController(db, new AccountRulesService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, uid.ToString()),
                }, "test")),
            },
        };
        var result = Assert.IsType<OkObjectResult>(await controller.List(family: true));
        // Как в ASP.NET Core: политика camelCase для JSON-ответа контроллера.
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task B15_AccountsList_QueryCountIsConstant_AndProjectionContractSurvives()
    {
        var small = await CreateFamilyDbAsync(1);
        using var c1 = small.Conn;
        await using var d1 = small.Db;
        var smallRows = await ListJsonAsync(d1, small.Me.Id);
        var smallCommands = small.Counter.Commands;

        var large = await CreateFamilyDbAsync(8);
        using var c2 = large.Conn;
        await using var d2 = large.Db;
        var largeRows = await ListJsonAsync(d2, large.Me.Id);
        var largeCommands = large.Counter.Commands;

        // Раньше проекция делала 2 запроса на счёт (владелец + банк).
        Assert.Equal(smallCommands, largeCommands);
        Assert.Equal(2, smallRows.GetArrayLength());  // по одному счёту на члена семьи
        Assert.Equal(16, largeRows.GetArrayLength());
        Assert.All(largeRows.EnumerateArray(), a =>
        {
            Assert.Equal("Т", a.GetProperty("bankShortName").GetString());
            Assert.Equal("Т-Банк", a.GetProperty("bankName").GetString());
            Assert.Equal("#FFDD2D", a.GetProperty("bankColor").GetString());
            Assert.True(a.TryGetProperty("interest", out _));
            Assert.True(a.TryGetProperty("available", out _));
            Assert.True(a.TryGetProperty("creditUsagePct", out _));
        });
        Assert.Equal("Я", largeRows[0].GetProperty("ownerName").GetString());
        Assert.False(largeRows[0].GetProperty("isFamily").GetBoolean());
        Assert.Equal("Супруг", largeRows[8].GetProperty("ownerName").GetString());
        Assert.True(largeRows[8].GetProperty("isFamily").GetBoolean());
        AssertPropertyNames(largeRows[0], "id", "name", "type", "typeName", "balance", "startingBalance",
            "creditLimit", "cashbackRate", "cashbackAccrued", "color", "isArchived", "ownerId", "ownerName",
            "isFamily", "bankId", "bankName", "bankShortName", "bankColor", "interest", "available",
            "creditUsagePct");
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }
}
