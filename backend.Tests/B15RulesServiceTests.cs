using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

// B15: оптимизация AccountRulesService — компактные проекции не меняют семантику,
// число запросов не зависит от количества правил и операций.
public class B15RulesServiceTests
{
    private sealed record RulesFixture(
        SqliteConnection Conn, AppDbContext Db, B15SqlCounter Counter, User User,
        Account Active, Account Archived, Category Source, Category Target);

    private static async Task<RulesFixture> CreateDbAsync(int rulesCount, int txCount)
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

        var user = new User { Email = "rules@b15.ru", Name = "Правила", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var active = new Account
        {
            UserId = user.Id, Name = "Активный", Type = AccountType.DebitCard,
            StartingBalance = 1000, Balance = 1000,
        };
        var archived = new Account
        {
            UserId = user.Id, Name = "Архивный", Type = AccountType.DebitCard,
            StartingBalance = 1000, Balance = 1000, IsArchived = true,
        };
        db.Accounts.AddRange(active, archived);
        await db.SaveChangesAsync();

        var source = new Category { UserId = user.Id, Name = "Прочее", Type = TxType.Expense };
        var target = new Category { UserId = user.Id, Name = "Продукты", Type = TxType.Expense };
        db.Categories.AddRange(source, target);
        await db.SaveChangesAsync();

        // Правило с приоритетом 1 ссылается на архивный счёт: матчингом пропускается.
        db.AccountRules.Add(new AccountRule
        {
            UserId = user.Id, Name = "Архивное", Kind = AccountRuleKind.AutoAssign,
            IsEnabled = true, Priority = 1, AccountId = archived.Id, CommentContains = "pizza",
        });
        for (var i = 0; i < rulesCount; i++)
        {
            db.AccountRules.Add(new AccountRule
            {
                UserId = user.Id, Name = $"Правило {i}", Kind = AccountRuleKind.AutoAssign,
                IsEnabled = true, Priority = 10 + i, AccountId = active.Id,
                CommentContains = "pizza", AmountMin = 100, AmountMax = 1000,
                SetCategoryId = target.Id,
            });
        }
        await db.SaveChangesAsync();

        for (var i = 0; i < txCount; i++)
        {
            db.Transactions.Add(new Transaction
            {
                UserId = user.Id, CategoryId = source.Id, Type = TxType.Expense,
                Amount = 500, Comment = $"Pizza {i}", Date = new DateOnly(2026, 11, 5),
            });
        }
        await db.SaveChangesAsync();

        counter.Commands = 0;
        return new RulesFixture(conn, db, counter, user, active, archived, source, target);
    }

    [Fact]
    public async Task B15_MatchAssignmentAsync_QueryCountIndependentOfRuleCount()
    {
        var few = await CreateDbAsync(rulesCount: 1, txCount: 0);
        using var c1 = few.Conn;
        await using var d1 = few.Db;
        var r1 = await new AccountRulesService(d1).MatchAssignmentAsync(few.User.Id,
            new AccountRulesService.RuleCandidate(TxType.Expense, 500, "Pizza", null));
        var fewCommands = few.Counter.Commands;

        var many = await CreateDbAsync(rulesCount: 12, txCount: 0);
        using var c2 = many.Conn;
        await using var d2 = many.Db;
        var r2 = await new AccountRulesService(d2).MatchAssignmentAsync(many.User.Id,
            new AccountRulesService.RuleCandidate(TxType.Expense, 500, "Pizza", null));
        var manyCommands = many.Counter.Commands;

        Assert.Equal(fewCommands, manyCommands);
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("Правило 0", r2!.RuleName);
        Assert.Equal(many.Active.Id, r2.AccountId);
        Assert.Equal(many.Target.Id, r2.SetCategoryId);
    }

    [Fact]
    public async Task B15_ApplyToExistingAsync_QueryCountIndependentOfTransactionCount_AndSemanticsKept()
    {
        var few = await CreateDbAsync(rulesCount: 2, txCount: 1);
        using var c1 = few.Conn;
        await using var d1 = few.Db;
        var dryFew = await new AccountRulesService(d1).ApplyToExistingAsync(few.User.Id,
            new AccountRulesService.ApplyOptions());
        var fewCommands = few.Counter.Commands;

        var many = await CreateDbAsync(rulesCount: 2, txCount: 10);
        using var c2 = many.Conn;
        await using var d2 = many.Db;
        var svc = new AccountRulesService(d2);
        var dryMany = await svc.ApplyToExistingAsync(many.User.Id,
            new AccountRulesService.ApplyOptions());
        var manyCommands = many.Counter.Commands;

        // Один запрос на правила, один на счета, один на категории, один на операции.
        Assert.Equal(fewCommands, manyCommands);
        Assert.Equal(1, dryFew.Matched);
        Assert.Equal(10, dryMany.Matched);
        Assert.Equal(0, dryMany.ChangedAccounts); // dry run ничего не меняет

        var real = await svc.ApplyToExistingAsync(many.User.Id,
            new AccountRulesService.ApplyOptions(DryRun: false));
        Assert.Equal(10, real.ChangedAccounts);
        Assert.Equal(10, real.ChangedCategories);
        var changed = await d2.Transactions.Where(t => t.AccountId == many.Active.Id)
            .ToListAsync();
        Assert.Equal(10, changed.Count);
        Assert.All(changed, t => Assert.Equal(many.Target.Id, t.CategoryId));
    }

    [Fact]
    public async Task B15_EvaluateAllAsync_CompactSelections_KeepAlertsSemantics()
    {
        var fixture = await CreateDbAsync(rulesCount: 0, txCount: 0);
        using var _ = fixture.Conn;
        await using var db = fixture.Db;
        var svc = new AccountRulesService(db);
        db.AccountRules.Add(new AccountRule
        {
            UserId = fixture.User.Id, Name = "Не ниже", Kind = AccountRuleKind.MinBalance,
            IsEnabled = true, AccountId = fixture.Active.Id, Threshold = 500,
        });
        await db.SaveChangesAsync();

        fixture.Active.Balance = 100;
        await svc.EvaluateAllAsync(fixture.User.Id);
        var alert = Assert.Single(await db.AccountAlerts
            .Where(a => a.UserId == fixture.User.Id && a.ResolvedAt == null).ToListAsync());
        Assert.Equal("MinBalance", alert.Kind);
        Assert.Equal(100m, alert.BalanceSnapshot);
        Assert.Contains("Активный", alert.Message, StringComparison.Ordinal);

        fixture.Active.Balance = 900;
        await svc.EvaluateAllAsync(fixture.User.Id);
        Assert.Empty(await db.AccountAlerts
            .Where(a => a.UserId == fixture.User.Id && a.ResolvedAt == null).ToListAsync());
    }
}
