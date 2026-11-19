using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>B4: параметры экспорта профиля (период, лимит, обезличивание).</summary>
public class ExportTests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db, User User)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Email = "ivan@example.com", Name = "Иван Петров", PasswordHash = "x:y",
            StartingBalance = 1000m,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (conn, db, user);
    }

    private static ExportController CreateController(AppDbContext db, int uid)
    {
        var controller = new ExportController(db, new AuditService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, uid.ToString())], "test")),
            },
        };
        return controller;
    }

    private static async Task<JsonDocument> ExportAsync(ExportController controller,
        string? from = null, string? to = null, bool anonymize = false, int? take = null)
    {
        var result = Assert.IsType<FileContentResult>(
            await controller.Profile(from, to, anonymize, take, CancellationToken.None));
        return JsonDocument.Parse(Encoding.UTF8.GetString(result.FileContents));
    }

    private static async Task<Category> AddCategoryAsync(AppDbContext db, int userId, string name)
    {
        var category = new Category { UserId = userId, Name = name, Type = TxType.Expense };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<Transaction> AddTxAsync(AppDbContext db, int userId, int categoryId,
        DateOnly date, decimal amount, string comment, int? accountId = null)
    {
        var tx = new Transaction
        {
            UserId = userId, CategoryId = categoryId, Type = TxType.Expense,
            Date = date, Amount = amount, Comment = comment, AccountId = accountId,
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        return tx;
    }

    private static string[] DatesOf(JsonDocument doc) => doc.RootElement
        .GetProperty("transactions").EnumerateArray()
        .Select(t => t.GetProperty("date").GetString()!).ToArray();

    [Fact]
    public async Task Default_WithoutParams_KeepsAllTransactions()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var category = await AddCategoryAsync(db, user.Id, "Продукты");
        await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 1, 1), 100m, "a");
        await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 6, 15), 200m, "b");
        await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 12, 31), 300m, "c");

        using var doc = await ExportAsync(CreateController(db, user.Id));
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("meta").GetProperty("schemaVersion").GetInt32());
        var p = root.GetProperty("meta").GetProperty("exportParams");
        Assert.Equal(JsonValueKind.Null, p.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("to").ValueKind);
        Assert.False(p.GetProperty("anonymize").GetBoolean());
        Assert.Equal(5000, p.GetProperty("take").GetInt32());
        Assert.False(p.GetProperty("truncated").GetBoolean());

        Assert.Equal(3, root.GetProperty("summary").GetProperty("transactionsCount").GetInt32());
        Assert.Equal(new[] { "2026-01-01", "2026-06-15", "2026-12-31" }, DatesOf(doc));
    }

    [Fact]
    public async Task DateRange_IsInclusive_AndFiltersCategoriesFromTransactions()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        // Общие категории другого пользователя попадают в экспорт только если
        // на них ссылаются отобранные операции — проверяем фильтрацию на IQueryable.
        var other = new User { Email = "other@example.com", Name = "Другой", PasswordHash = "x:y" };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        var sharedA = await AddCategoryAsync(db, other.Id, "Общая А");
        var sharedB = await AddCategoryAsync(db, other.Id, "Общая Б");

        await AddTxAsync(db, user.Id, sharedA.Id, new DateOnly(2026, 11, 1), 100m, "a");
        await AddTxAsync(db, user.Id, sharedB.Id, new DateOnly(2026, 11, 13), 200m, "b");
        await AddTxAsync(db, user.Id, sharedB.Id, new DateOnly(2026, 11, 30), 300m, "c");
        await AddTxAsync(db, user.Id, sharedA.Id, new DateOnly(2026, 12, 1), 400m, "d");

        var controller = CreateController(db, user.Id);
        using (var doc = await ExportAsync(controller, "2026-11-13", "2026-11-30"))
        {
            // обе границы включены
            Assert.Equal(new[] { "2026-11-13", "2026-11-30" }, DatesOf(doc));
            var p = doc.RootElement.GetProperty("meta").GetProperty("exportParams");
            Assert.Equal("2026-11-13", p.GetProperty("from").GetString());
            Assert.Equal("2026-11-30", p.GetProperty("to").GetString());

            var categoryIds = doc.RootElement.GetProperty("categories").EnumerateArray()
                .Select(c => c.GetProperty("id").GetInt32()).ToArray();
            Assert.Contains(sharedB.Id, categoryIds);
            Assert.DoesNotContain(sharedA.Id, categoryIds);
        }

        // category-фильтр считается по уже ограниченному take наборе операций
        using (var doc = await ExportAsync(controller, "2026-11-01", "2026-11-30", take: 1))
        {
            Assert.Equal(new[] { "2026-11-01" }, DatesOf(doc));
            var categoryIds = doc.RootElement.GetProperty("categories").EnumerateArray()
                .Select(c => c.GetProperty("id").GetInt32()).ToArray();
            Assert.Contains(sharedA.Id, categoryIds);
            Assert.DoesNotContain(sharedB.Id, categoryIds);
        }
    }

    [Fact]
    public async Task Take_IsClamped_AndTruncatedReflectsLoadedCount()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var category = await AddCategoryAsync(db, user.Id, "Продукты");
        for (var i = 1; i <= 5; i++)
            await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 11, i), i * 10m, $"операция {i}");

        var controller = CreateController(db, user.Id);

        using (var doc = await ExportAsync(controller, take: 2))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("transactions").GetArrayLength());
            var p = doc.RootElement.GetProperty("meta").GetProperty("exportParams");
            Assert.Equal(2, p.GetProperty("take").GetInt32());
            Assert.True(p.GetProperty("truncated").GetBoolean());
        }

        using (var doc = await ExportAsync(controller, take: 0))
        {
            Assert.Single(DatesOf(doc)); // 0 → 1
            Assert.Equal(1, doc.RootElement.GetProperty("meta").GetProperty("exportParams")
                .GetProperty("take").GetInt32());
        }

        using (var doc = await ExportAsync(controller, take: -7))
        {
            Assert.Single(DatesOf(doc)); // отрицательное → 1
        }

        using (var doc = await ExportAsync(controller, take: 9999))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("transactions").GetArrayLength());
            var p = doc.RootElement.GetProperty("meta").GetProperty("exportParams");
            Assert.Equal(5000, p.GetProperty("take").GetInt32());
            Assert.False(p.GetProperty("truncated").GetBoolean());
        }
    }

    [Fact]
    public async Task Anonymize_RemovesPersonalValues_KeepsAmountsAndDates()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var account = new Account
        {
            UserId = user.Id, Name = "Карта Ивана", Type = AccountType.DebitCard,
            Balance = 1000m, StartingBalance = 1000m,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var category = await AddCategoryAsync(db, user.Id, "Продукты Ивана");
        await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 11, 13), 1234.56m,
            "СЕКРЕТНЫЙ КОММЕНТ ИВАНА", account.Id);

        db.SavingsGoals.Add(new SavingsGoal
        {
            UserId = user.Id, Name = "Отпуск Ивана", TargetAmount = 100000m, CurrentAmount = 1000m,
        });
        db.AccountRules.Add(new AccountRule
        {
            UserId = user.Id, Name = "Правило Ивана", CommentContains = "Иван",
            Kind = AccountRuleKind.AutoAssign, Priority = 1,
        });
        db.AccountAlerts.Add(new AccountAlert
        {
            UserId = user.Id, Message = "Зарплата Ивана", Kind = "MinBalance", Level = "warn",
        });
        db.ReserveLoans.Add(new ReserveLoan
        {
            UserId = user.Id, AccountId = account.Id, Amount = 500m,
            DueDate = DateTime.UtcNow.AddDays(10), Comment = "ДОЛГ ИВАНА",
        });

        var bank = new Bank { Name = "Т-Банк Ивана", Code = "tbank" };
        db.Banks.Add(bank);
        await db.SaveChangesAsync();
        var portfolio = new InvestmentPortfolio
        {
            UserId = user.Id, Name = "Портфель Ивана", BankId = bank.Id,
        };
        db.InvestmentPortfolios.Add(portfolio);
        await db.SaveChangesAsync();
        db.InvestmentHoldings.Add(new InvestmentHolding
        {
            UserId = user.Id, PortfolioId = portfolio.Id, Name = "Акция Ивана",
            Ticker = "IVAN", Quantity = 1m, AvgPrice = 100m,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);
        using var doc = await ExportAsync(controller, "2026-11-01", "2026-11-30", anonymize: true, take: 500);
        var raw = Encoding.UTF8.GetString(
            Assert.IsType<FileContentResult>(await controller.Profile("2026-11-01", "2026-11-30", true, 500, CancellationToken.None)).FileContents);
        var root = doc.RootElement;

        // Ни одного персонального значения в сериализованном JSON.
        foreach (var secret in new[]
                 {
                     "ivan@example.com", "Иван Петров", "Карта Ивана", "Продукты Ивана",
                     "СЕКРЕТНЫЙ КОММЕНТ ИВАНА", "Отпуск Ивана", "Правило Ивана",
                     "Зарплата Ивана", "ДОЛГ ИВАНА", "Т-Банк Ивана", "Портфель Ивана",
                     "Акция Ивана", "IVAN", "Иван",
                 })
            Assert.DoesNotContain(secret, raw);

        var userNode = root.GetProperty("user");
        Assert.StartsWith("user-", userNode.GetProperty("id").GetString()!);
        Assert.NotEqual(user.Id.ToString(), userNode.GetProperty("id").GetString());

        var p = root.GetProperty("meta").GetProperty("exportParams");
        Assert.True(p.GetProperty("anonymize").GetBoolean());
        Assert.Equal(500, p.GetProperty("take").GetInt32());

        // Сумма и дата сохранены.
        var tx = Assert.Single(root.GetProperty("transactions").EnumerateArray());
        Assert.Equal("2026-11-13", tx.GetProperty("date").GetString());
        Assert.Equal(1234.56m, tx.GetProperty("amount").GetDecimal());

        // Стабильность псевдонима между вызовами.
        using var doc2 = await ExportAsync(controller, anonymize: true);
        Assert.Equal(userNode.GetProperty("id").GetString(),
            doc2.RootElement.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Audit_ContainsOnlyRequestParams_AndNoPersonalValues()
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var category = await AddCategoryAsync(db, user.Id, "Продукты");
        await AddTxAsync(db, user.Id, category.Id, new DateOnly(2026, 11, 13), 100m, "СЕКРЕТ");

        var controller = CreateController(db, user.Id);
        using var doc = await ExportAsync(controller, "2026-11-01", "2026-11-30", anonymize: true, take: 500);
        Assert.False(doc.RootElement.GetProperty("meta").GetProperty("exportParams")
            .GetProperty("truncated").GetBoolean());

        var log = Assert.Single(await db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditService.ExportProfile, log.Action);
        Assert.Contains("from=2026-11-01", log.Details);
        Assert.Contains("to=2026-11-30", log.Details);
        Assert.Contains("take=500", log.Details);
        Assert.Contains("anonymize=True", log.Details);
        Assert.DoesNotContain("ivan@example.com", log.Details);
        Assert.DoesNotContain("Иван", log.Details);
        Assert.DoesNotContain("СЕКРЕТ", log.Details);
    }

    [Theory]
    [InlineData("13.11.2026", "2026-11-30")]
    [InlineData("2026-11-13", "30.11.2026")]
    [InlineData("2026-12-01", "2026-11-30")]
    public async Task InvalidParams_ReturnBadRequest(string? from, string? to)
    {
        var (conn, db, user) = await CreateDbAsync();
        using var _ = conn;
        using var __ = db;

        var result = await CreateController(db, user.Id).Profile(from, to, false, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
