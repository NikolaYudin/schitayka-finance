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

// Тесты этапа A: архивные счета, лимиты по годам, исключение переводов из аналитики.
public class StageATests
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

    private static async Task<JsonElement> JsonOfAsync<T>(Task<T> action) where T : IActionResult
    {
        var result = await action;
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, ok.Value?.GetType() ?? typeof(object));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Summary_ExcludesTransfers_FromIncomeExpenseAndSavingsRate()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var salary = new Category { UserId = user.Id, Name = "Зарплата", Type = TxType.Income };
        var products = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        var account = new Account
        {
            UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard,
            StartingBalance = 80000, Balance = 80000,
        };
        db.AddRange(salary, products, account);
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 11, 5);
        db.Transactions.AddRange(
            new Transaction { UserId = user.Id, CategoryId = salary.Id, Type = TxType.Income, Amount = 100000, Date = date, AccountId = account.Id },
            new Transaction { UserId = user.Id, CategoryId = products.Id, Type = TxType.Expense, Amount = 20000, Date = date, AccountId = account.Id },
            new Transaction { UserId = user.Id, CategoryId = products.Id, Type = TxType.Income, Amount = 50000, Date = date, AccountId = account.Id, IsTransfer = true },
            new Transaction { UserId = user.Id, CategoryId = products.Id, Type = TxType.Expense, Amount = 50000, Date = date, AccountId = account.Id, IsTransfer = true });
        await db.SaveChangesAsync();

        var controller = WithUser(new BudgetController(db, new AccountRulesService(db)), user.Id);
        var summary = await JsonOfAsync(controller.Summary(2026, 11));

        Assert.Equal(100000m, summary.GetProperty("monthIncome").GetDecimal());
        Assert.Equal(20000m, summary.GetProperty("monthExpense").GetDecimal());
        Assert.Equal(80000m, summary.GetProperty("balance").GetDecimal());
        Assert.Equal(80m, summary.GetProperty("savingsRate").GetDecimal());

        var month = Assert.Single(summary.GetProperty("byMonth").EnumerateArray());
        Assert.Equal(100000m, month.GetProperty("income").GetDecimal());
        Assert.Equal(20000m, month.GetProperty("expense").GetDecimal());
    }

    [Fact]
    public async Task BudgetLimits_AllowSameMonthInDifferentYears_ButRejectDuplicates()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var cat = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();

        db.BudgetLimits.Add(new BudgetLimit { UserId = user.Id, CategoryId = cat.Id, Year = 2025, Month = 11, Limit = 1000 });
        db.BudgetLimits.Add(new BudgetLimit { UserId = user.Id, CategoryId = cat.Id, Year = 2026, Month = 11, Limit = 2000 });
        await db.SaveChangesAsync();

        db.BudgetLimits.Add(new BudgetLimit { UserId = user.Id, CategoryId = cat.Id, Year = 2026, Month = 11, Limit = 3000 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AccountArchive_SetsFlag_ExcludesFromList_ButStillRecalculatesBalance()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var expense = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        var account = new Account
        {
            UserId = user.Id, Name = "Старая карта", Type = AccountType.DebitCard,
            StartingBalance = 100, Balance = 0,
        };
        db.AddRange(expense, account);
        await db.SaveChangesAsync();
        db.Transactions.Add(new Transaction
        {
            UserId = user.Id, CategoryId = expense.Id, Type = TxType.Expense,
            Amount = 30, Date = new DateOnly(2026, 11, 1), AccountId = account.Id,
        });
        await db.SaveChangesAsync();

        var controller = WithUser(new AccountsController(db, new AccountRulesService(db)), user.Id);
        var dto = new AccountsController.AccountDto(
            account.Name, account.Type, account.StartingBalance, 0, 0, account.Color, Archived: true);
        await controller.Update(account.Id, dto);

        var stored = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == account.Id);
        Assert.True(stored.IsArchived);
        Assert.Equal(70m, stored.Balance); // 100 − 30, архивные тоже пересчитываются

        var list = await JsonOfAsync(controller.List(includeArchived: false));
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task TransactionUpdate_PersistsTransferFlag()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var cat = new Category { UserId = user.Id, Name = "Перевод", Type = TxType.Expense };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        var tx = new Transaction
        {
            UserId = user.Id, CategoryId = cat.Id, Type = TxType.Expense,
            Amount = 5000, Date = new DateOnly(2026, 11, 2),
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();

        var controller = WithUser(new TransactionsController(db, new AccountRulesService(db)), user.Id);
        var dto = new TransactionsController.TxDto(
            cat.Id, TxType.Expense, 5000, new DateOnly(2026, 11, 2), "Перевод", null, IsTransfer: true);
        await controller.Update(tx.Id, dto);

        var stored = await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        Assert.True(stored.IsTransfer);
    }
}
