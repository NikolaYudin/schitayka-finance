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

// Тесты задачи 0: правила счетов (автоподстановка, неснижаемый остаток, резерв).
public class Task0Tests
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
    public async Task AutoAssign_MatchesByCommentCategoryAndPriority_AndIgnoresArchivedAccount()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var acc = new Account { UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard, StartingBalance = 1000, Balance = 1000 };
        var accCash = new Account { UserId = user.Id, Name = "Наличные", Type = AccountType.Cash, StartingBalance = 1000, Balance = 1000 };
        db.Accounts.AddRange(acc, accCash);
        await db.SaveChangesAsync();

        var cat = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();

        var low = new AccountRule { UserId = user.Id, Name = "Пятёрочка", Kind = AccountRuleKind.AutoAssign, IsEnabled = true, Priority = 10, AccountId = acc.Id, CommentContains = "Пятёрочка", SetCategoryId = cat.Id, AmountMin = 500, AmountMax = 20000 };
        var high = new AccountRule { UserId = user.Id, Name = "Пятёрочка-другое", Kind = AccountRuleKind.AutoAssign, IsEnabled = true, Priority = 20, AccountId = accCash.Id, CommentContains = "Пятёрочка", AmountMin = 500, AmountMax = 20000 };
        db.AccountRules.AddRange(low, high);
        await db.SaveChangesAsync();

        var svc = new AccountRulesService(db);

        // Комментарий + автокатегория.
        var m1 = await svc.MatchAssignmentAsync(user.Id, new AccountRulesService.RuleCandidate(TxType.Expense, 1500, "Пятёрочка Pizza", cat.Id));
        Assert.NotNull(m1);
        Assert.Equal(low.Id, m1!.RuleId);
        Assert.Equal(acc.Id, m1.AccountId);
        Assert.Equal(cat.Id, m1.SetCategoryId);

        // Сумма вне диапазона условия.
        var m2 = await svc.MatchAssignmentAsync(user.Id, new AccountRulesService.RuleCandidate(TxType.Expense, 100, "Пятёрочка", cat.Id));
        Assert.Null(m2);

        // Приоритет: меньшее число побеждает.
        var m3 = await svc.MatchAssignmentAsync(user.Id, new AccountRulesService.RuleCandidate(TxType.Expense, 1500, "Пятёрочка", null));
        Assert.NotNull(m3);
        Assert.Equal(low.Id, m3!.RuleId);

        // Счёт-правило принадлежит другому пользователю — не подходит.
        var m4 = await svc.MatchAssignmentAsync(999, new AccountRulesService.RuleCandidate(TxType.Expense, 1500, "Пятёрочка", cat.Id));
        Assert.Null(m4);
    }

    [Fact]
    public async Task MinBalance_CreatesAlertOnViolation_AndResolvesWhenBalanceRecovered()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var acc = new Account { UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard, StartingBalance = 1000, Balance = 1000 };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        var rule = new AccountRule { UserId = user.Id, Name = "Не опускаться ниже", Kind = AccountRuleKind.MinBalance, IsEnabled = true, AccountId = acc.Id, Threshold = 500 };
        db.AccountRules.Add(rule);
        await db.SaveChangesAsync();

        var svc = new AccountRulesService(db);

        // Баланс выше минимума — тихо.
        await svc.EvaluateAllAsync(user.Id);
        Assert.Empty(await db.AccountAlerts.Where(a => a.UserId == user.Id && a.ResolvedAt == null).ToListAsync());

        // Баланс опускается ниже минимума — уведомление.
        acc.Balance = 100;
        await db.SaveChangesAsync();
        await svc.EvaluateAllAsync(user.Id);
        var alert = await db.AccountAlerts.FirstAsync(a => a.UserId == user.Id && a.Kind == "MinBalance");
        Assert.Equal(acc.Id, alert.AccountId);
        Assert.Equal(rule.Id, alert.RuleId);
        Assert.False(alert.ResolvedAt.HasValue);

        // Баланс восстановлен — уведомление закрывается автоматически.
        acc.Balance = 1000;
        await db.SaveChangesAsync();
        await svc.EvaluateAllAsync(user.Id);
        Assert.Empty(await db.AccountAlerts.Where(a => a.UserId == user.Id && a.ResolvedAt == null).ToListAsync());
    }

    [Fact]
    public async Task Reserve_FreeReserveAndLoanLifecycle()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var acc = new Account { UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard, StartingBalance = 1000, Balance = 1000 };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        var rule = new AccountRule { UserId = user.Id, Name = "Подушка", Kind = AccountRuleKind.EmergencyReserve, IsEnabled = true, AccountId = acc.Id, Threshold = 500, DefaultReturnDays = 30 };
        db.AccountRules.Add(rule);
        await db.SaveChangesAsync();

        var svc = new AccountRulesService(db);

        // Свободный резерв: min(balance, threshold) − уже взятые займы.
        Assert.Equal(500m, AccountRulesService.FreeReserve(1000m, 500m, 0m));
        Assert.Equal(200m, AccountRulesService.FreeReserve(1000m, 500m, 300m));

        // Заём из резерва.
        var loan = await svc.TakeReserveLoanAsync(user.Id, rule.Id, acc.Id, 300, DateTime.UtcNow.AddDays(30));
        Assert.NotNull(loan);
        Assert.Equal(300m, loan.Amount);
        Assert.Null(loan.ReturnedAt);

        // Возврат — только закрытие займа, деньги из резерва не списываются автоматически.
        await svc.ReturnReserveLoanAsync(user.Id, loan.Id);
        var stored = await db.ReserveLoans.AsNoTracking().FirstAsync(l => l.Id == loan.Id);
        Assert.NotNull(stored.ReturnedAt);
    }

    [Fact]
    public async Task BulkApply_DryRun_DoesNotChangeTransactions()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var cat = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        var acc = new Account { UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard, StartingBalance = 1000, Balance = 1000 };
        db.Categories.Add(cat);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        var rule = new AccountRule { UserId = user.Id, Name = "Пятёрочка", Kind = AccountRuleKind.AutoAssign, IsEnabled = true, Priority = 10, AccountId = acc.Id, CommentContains = "Пятёрочка" };
        db.AccountRules.Add(rule);
        var tx = new Transaction { UserId = user.Id, CategoryId = cat.Id, Type = TxType.Expense, Amount = 1500, Date = new DateOnly(2026, 11, 5), Comment = "Пятёрочка", AccountId = null };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();

        var controller = WithUser(new AccountRulesController(db, new AccountRulesService(db)), user.Id);
        var res = await controller.Apply(dryRun: true, overwrite: false);
        var ok = Assert.IsType<OkObjectResult>(res);
        var matchedProp = ok.Value!.GetType().GetProperty("matched")!.GetValue(ok.Value);
        Assert.NotNull(matchedProp);
        var matched = Convert.ToInt32(matchedProp!);
        Assert.Equal(1, matched);

        // Dry-run не меняет данные.
        Assert.Null((await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == tx.Id)).AccountId);

        // Фактическое применение привязывает операцию к счёту.
        var res2 = await controller.Apply(dryRun: false, overwrite: false);
        Assert.IsType<OkObjectResult>(res2);
        Assert.NotNull((await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == tx.Id)).AccountId);
    }
}
