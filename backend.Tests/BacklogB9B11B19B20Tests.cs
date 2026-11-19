using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SchitayKa.Api.Controllers;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Регрессионные тесты бэклога B9/B10/B19/B20.</summary>
public class BacklogB9B11B19B20Tests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db, User User, Category Cat)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Email = "test@schitayka.ru", Name = "Тест",
            PasswordHash = AuthController.HashPassword("old-pass-123"),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var cat = new Category { UserId = user.Id, Name = "Прочее", Type = TxType.Expense };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        return (conn, db, user, cat);
    }

    private static async Task<Account> AddAccountAsync(
        AppDbContext db, User user, string name, decimal start, AccountType type = AccountType.DebitCard)
    {
        var acc = new Account
        {
            UserId = user.Id, Name = name, Type = type,
            StartingBalance = start, Balance = start,
            CreditLimit = type == AccountType.CreditCard ? 100_000m : 0m,
        };
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();
        return acc;
    }

    // ---------------- B19: знаковый остаток ----------------

    [Fact]
    public void Compute_KeepsOverdraftOnDebitAccount()
    {
        var acc = new Account { Type = AccountType.DebitCard, StartingBalance = 100m };
        var txs = new[] { new AccountBalanceService.BalanceTx(TxType.Expense, 350m, false) };

        var (balance, _) = AccountBalanceService.Compute(acc, txs);

        Assert.Equal(-250m, balance);
    }

    [Fact]
    public void Compute_KeepsOverpaymentOnCreditCard()
    {
        var acc = new Account { Type = AccountType.CreditCard, StartingBalance = 500m };
        var txs = new[] { new AccountBalanceService.BalanceTx(TxType.Income, 700m, true) };

        var (balance, _) = AccountBalanceService.Compute(acc, txs);

        Assert.Equal(-200m, balance);
    }

    // ---------------- B9: смена пароля ----------------

    [Fact]
    public async Task ChangePassword_RejectsWrongCurrentAndInvalidatesOldJwtVersion()
    {
        var (conn, db, user, _) = await CreateDbAsync();
        using var _conn = conn;
        using var _db = db;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new AuthController(db, cfg, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    }, "test")),
                },
            },
        };

        var wrong = await controller.ChangePassword(
            new ChangePasswordDto("wrong-current", "new-pass-456"));
        Assert.IsType<BadRequestObjectResult>(wrong);
        Assert.Equal(0, user.PasswordVersion);

        var ok = await controller.ChangePassword(
            new ChangePasswordDto("old-pass-123", "new-pass-456"));
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(1, user.PasswordVersion);
        Assert.True(AuthController.VerifyPassword("new-pass-456", user.PasswordHash));

        var audit = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, audit.Count); // неудачная и удачная попытки
        Assert.Contains(AuditService.PasswordFailed, audit[0].Action);
        Assert.Contains(AuditService.PasswordChanged, audit[1].Action);
    }

    // ---------------- B10: журнал аудита ----------------

    [Fact]
    public async Task AuditService_WritesActionWithoutSensitiveData()
    {
        var (conn, db, user, _) = await CreateDbAsync();
        using var _conn = conn;
        using var _db = db;

        var service = new AuditService(db);
        await service.LogAsync(user.Id, AuditService.ImportReceipt,
            "файл чек.jpg, 1024 байт, OCR успешно", new DefaultHttpContext());

        var row = await db.AuditLogs.SingleAsync();
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(AuditService.ImportReceipt, row.Action);
        Assert.Contains("чек.jpg", row.Details);
    }

    // ---------------- B20: историческая пара переводов ----------------

    private static async Task AddLegacyPairAsync(
        AppDbContext db, User user, Category cat, Account from, Account to,
        DateOnly date, decimal amount, string comment, int copies = 1)
    {
        for (var i = 0; i < copies; i++)
        {
            db.Transactions.Add(new Transaction
            {
                UserId = user.Id, CategoryId = cat.Id, Type = TxType.Expense,
                Amount = amount, Date = date, Comment = comment,
                AccountId = from.Id, IsTransfer = true,
            });
            db.Transactions.Add(new Transaction
            {
                UserId = user.Id, CategoryId = cat.Id, Type = TxType.Income,
                Amount = amount, Date = date, Comment = comment,
                AccountId = to.Id, IsTransfer = true,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteOneHalfOfLegacyTransfer_RemovesBothAndRestoresBalances()
    {
        var (conn, db, user, cat) = await CreateDbAsync();
        using var _conn = conn;
        using var _db = db;

        var from = await AddAccountAsync(db, user, "Карта", 10_000m);
        var to = await AddAccountAsync(db, user, "Копилка", 1_000m);
        var date = new DateOnly(2026, 11, 5);
        await AddLegacyPairAsync(db, user, cat, from, to, date, 3_000m, "Перевод на копилку");

        var expense = await db.Transactions.FirstAsync(t => t.Type == TxType.Expense);
        var service = new TransferService(db);
        var result = await service.CancelByTransactionDetailedAsync(user.Id, expense.Id);

        Assert.Null(result.Error);
        Assert.NotNull(result.TransferId);
        Assert.Empty(await db.Transactions.ToListAsync());

        await AccountBalanceService.RecalcAsync(db, new[] { from.Id, to.Id });
        Assert.Equal(10_000m, (await db.Accounts.FindAsync(from.Id))!.Balance);
        Assert.Equal(1_000m, (await db.Accounts.FindAsync(to.Id))!.Balance);
    }

    [Fact]
    public async Task DeleteLegacyTransfer_WithSeveralMatchingPairs_ReportsAmbiguity()
    {
        var (conn, db, user, cat) = await CreateDbAsync();
        using var _conn = conn;
        using var _db = db;

        var from = await AddAccountAsync(db, user, "Карта", 10_000m);
        var to = await AddAccountAsync(db, user, "Копилка", 1_000m);
        var date = new DateOnly(2026, 11, 6);
        // Две пары с одинаковыми суммой/датой/комментарием — нельзя угадать, какую удалять.
        await AddLegacyPairAsync(db, user, cat, from, to, date, 500m, "Одинаковый перевод", copies: 2);

        var expense = await db.Transactions.FirstAsync(t => t.Type == TxType.Expense);
        var result = await new TransferService(db)
            .CancelByTransactionDetailedAsync(user.Id, expense.Id);

        Assert.Null(result.TransferId);
        Assert.Contains("несколько", result.Error!);
        Assert.Equal(4, await db.Transactions.CountAsync()); // ничего не удалено
    }
}
