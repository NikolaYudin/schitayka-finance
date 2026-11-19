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

/// <summary>Мастер-профиль, администратор семьи, настройки и ретро-применение правил.</summary>
public class AdminAndSettingsTests
{
    private static async Task<(SqliteConnection Conn, AppDbContext Db)> CreateDbAsync(bool withMaster = true)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        if (withMaster)
        {
            db.Users.Add(new User
            {
                Email = "master@schitayka.local", Name = "Мастер системы",
                PasswordHash = "", Role = UserRole.Master, IsSystemMaster = true,
            });
            await db.SaveChangesAsync();
        }
        return (conn, db);
    }

    private static ControllerContext Context(int uid, string? role = null, string? email = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, uid.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-key-for-local-verification-12345678901234567890",
            ["Jwt:Issuer"] = "SchitayKa", ["Jwt:Audience"] = "SchitayKaClients",
        }).Build();

    [Fact]
    public async Task Setup_CreatesAdminFromMasterProfile_AndCannotBeRepeated()
    {
        var (conn, db) = await CreateDbAsync();
        using var _c = conn;
        using var _d = db;
        var audit = new AuditService(db);
        var setup = new SetupController(db, audit, Config());

        var status = await setup.Status();
        Assert.True(Assert.IsType<OkObjectResult>(status).Value is { } s &&
                    s.GetType().GetProperty("required")?.GetValue(s) is true);

        var result = await setup.CreateAdmin(new SetupController.AdminDto(
            "admin@family.ru", "Администратор", "strong-pass-1"));
        var ok = Assert.IsType<OkObjectResult>(result);
        var admin = await db.Users.SingleAsync();
        Assert.Equal(UserRole.Master, admin.Role);
        Assert.False(admin.IsSystemMaster);
        Assert.Equal("admin@family.ru", admin.Email);
        Assert.True(await db.Categories.AnyAsync(c => c.UserId == admin.Id));

        var repeat = await setup.CreateAdmin(new SetupController.AdminDto(
            "other@family.ru", "Другой", "strong-pass-2"));
        Assert.IsType<ConflictObjectResult>(repeat);

        var after = await setup.Status();
        Assert.False(Assert.IsType<OkObjectResult>(after).Value is { } a &&
                     a.GetType().GetProperty("required")?.GetValue(a) is true);
    }

    [Fact]
    public async Task Admin_CreatesFamilyUser_AndResetsPassword()
    {
        var (conn, db) = await CreateDbAsync();
        using var _c = conn;
        using var _d = db;
        var admin = new User
        {
            Email = "admin@family.ru", Name = "Администратор",
            PasswordHash = AuthController.HashPassword("strong-pass-1"),
            Role = UserRole.Master, FamilyCode = "FAM-TEST01",
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        db.Users.Remove(db.Users.Single(u => u.IsSystemMaster));
        await db.SaveChangesAsync();

        var audit = new AuditService(db);
        var controller = new AdminUsersController(db, audit)
        {
            ControllerContext = Context(admin.Id, "Master", admin.Email),
        };

        var created = await controller.Create(new AdminUsersController.CreateUserDto(
            "member@family.ru", "Участник", "member-pass-1"));
        Assert.IsType<OkObjectResult>(created);
        var member = await db.Users.SingleAsync(u => u.Email == "member@family.ru");
        Assert.Equal(UserRole.Member, member.Role);
        Assert.Equal(admin.FamilyCode, member.FamilyCode);
        Assert.Equal(admin.Id, member.CreatedByUserId);
        Assert.True(await db.Categories.AnyAsync(c => c.UserId == member.Id));

        var reset = await controller.ResetPassword(member.Id,
            new AdminUsersController.ResetPasswordDto("member-pass-2"));
        Assert.IsType<OkObjectResult>(reset);
        Assert.True(AuthController.VerifyPassword("member-pass-2", member.PasswordHash));
        Assert.Equal(1, member.PasswordVersion);
    }

    [Fact]
    public async Task Settings_StoreThemePerUser()
    {
        var (conn, db) = await CreateDbAsync();
        using var _c = conn;
        using var _d = db;
        var user = new User { Email = "u@family.ru", Name = "Тест", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new SettingsController(db) { ControllerContext = Context(user.Id) };
        var saved = await controller.SetAppearance(new SettingsController.AppearanceDto("dark"));
        Assert.Equal("dark", Assert.IsType<OkObjectResult>(saved).Value is { } v
            ? v.GetType().GetProperty("theme")?.GetValue(v) : null);

        var loaded = await controller.GetAppearance();
        Assert.Equal("dark", Assert.IsType<OkObjectResult>(loaded).Value is { } r
            ? r.GetType().GetProperty("theme")?.GetValue(r) : null);

        await controller.SetAppearance(new SettingsController.AppearanceDto("неизвестно"));
        Assert.Equal("system", (await db.Users.FindAsync(user.Id))!.Theme);
    }

    [Fact]
    public async Task ApplyRules_DryRunDoesNotChange_AndRealRunAssignsAccount()
    {
        var (conn, db) = await CreateDbAsync(false);
        using var _c = conn;
        using var _d = db;
        var user = new User { Email = "u@family.ru", Name = "Тест", PasswordHash = "x:y" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var category = new Category { UserId = user.Id, Name = "Прочее", Type = TxType.Expense };
        var account = new Account
        {
            UserId = user.Id, Name = "Карта", Type = AccountType.DebitCard,
            StartingBalance = 100m, Balance = 100m,
        };
        db.AddRange(category, account);
        await db.SaveChangesAsync();
        db.Transactions.Add(new Transaction
        {
            UserId = user.Id, CategoryId = category.Id, Type = TxType.Expense,
            Amount = 100m, Date = new DateOnly(2026, 11, 1), Comment = "Кофе", AccountId = null,
        });
        db.AccountRules.Add(new AccountRule
        {
            UserId = user.Id, Name = "Кофе на карту", Kind = AccountRuleKind.AutoAssign,
            IsEnabled = true, Priority = 1, AccountId = account.Id,
            CommentContains = "кофе",
        });
        await db.SaveChangesAsync();

        var service = new AccountRulesService(db);
        var dry = await service.ApplyToExistingAsync(user.Id, new AccountRulesService.ApplyOptions());
        Assert.Equal(1, dry.Matched);
        Assert.Equal(0, dry.ChangedAccounts);
        Assert.Null((await db.Transactions.SingleAsync()).AccountId);

        var real = await service.ApplyToExistingAsync(user.Id,
            new AccountRulesService.ApplyOptions(DryRun: false));
        Assert.Equal(account.Id, (await db.Transactions.SingleAsync()).AccountId);
        Assert.Equal(1, real.ChangedAccounts);
    }
}
