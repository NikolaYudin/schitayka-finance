using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

// Тесты исправлений P0: семейная операция и выплата процентов.
// Раньше эти эндпоинты не вызывались напрямую — поэтому 500 и некорректная
// выплата не ловились ни тестами, ни UI.
public class EndpointFixesTests
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

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, "test"));

    private static T WithUser<T>(T controller, int userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    [Fact]
    public async Task CreateTxForFamily_RecalculatesAccountAndReturnsOk()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var owner = new User { Email = "owner@test.ru", Name = "Владелец", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var partner = new User { Email = "partner@test.ru", Name = "Партнёр", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        db.Users.AddRange(owner, partner);
        await db.SaveChangesAsync();

        var cat = new Category { UserId = partner.Id, Name = "Продукты", Type = TxType.Expense };
        var acc = new Account
        {
            UserId = partner.Id, Name = "Карта", Type = AccountType.DebitCard,
            StartingBalance = 1000, Balance = 1000,
        };
        db.Categories.Add(cat);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        var controller = WithUser(
            new BudgetController(db, new AccountRulesService(db)), owner.Id);
        var dto = new BudgetController.TxCreateDto(
            cat.Id, TxType.Expense, 100, new DateOnly(2026, 11, 2), "обед", acc.Id, partner.Id);

        var result = await controller.CreateTxForFamily(dto);

        Assert.IsType<OkObjectResult>(result);
        var storedTx = await db.Transactions.AsNoTracking().SingleAsync(t => t.UserId == partner.Id);
        Assert.Equal(100m, storedTx.Amount);
        var storedAcc = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == acc.Id);
        Assert.Equal(900m, storedAcc.Balance);
    }

    [Fact]
    public async Task Payout_PaysExactPendingAmount_AndRejectsSecondPayout()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var user = new User { Email = "user@test.ru", Name = "Пользователь", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var acc = new Account
        {
            UserId = user.Id, Name = "Вклад", Type = AccountType.Deposit,
            StartingBalance = 100000, Balance = 100000,
        };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        db.InterestAccruals.AddRange(
            new InterestAccrual { UserId = user.Id, AccountId = acc.Id, From = DateTime.UtcNow, To = DateTime.UtcNow, Days = 30, Gross = 115m, Tax = 15m, Net = 100m },
            new InterestAccrual { UserId = user.Id, AccountId = acc.Id, From = DateTime.UtcNow, To = DateTime.UtcNow, Days = 30, Gross = 57.5m, Tax = 7.5m, Net = 50m });
        await db.SaveChangesAsync();

        var controller = WithUser(new InterestController(db), user.Id);

        // Произвольная сумма меньше начисленной — отказ, журнал не закрывается.
        var mismatch = await controller.Payout(acc.Id, new InterestController.PayoutDto(Amount: 10m));
        Assert.IsType<BadRequestObjectResult>(mismatch);
        Assert.Equal(2, await db.InterestAccruals.CountAsync(a => !a.IsPaid));

        // Сумма не передана — выплачивается ровно сумма Net неоплаченных начислений.
        var ok = await controller.Payout(acc.Id, new InterestController.PayoutDto());
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(0, await db.InterestAccruals.CountAsync(a => !a.IsPaid));

        var income = await db.Transactions.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(150m, income.Amount);
        Assert.Equal(TxType.Income, income.Type);

        var storedAcc = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == acc.Id);
        Assert.Equal(100150m, storedAcc.Balance);

        // Повторная выплата: начислений больше нет — отказ, второй доход не создаётся.
        var second = await controller.Payout(acc.Id, new InterestController.PayoutDto());
        Assert.IsType<BadRequestObjectResult>(second);
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.UserId == user.Id));
    }
}
