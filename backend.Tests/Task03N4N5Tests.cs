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

/// <summary>
/// Задачи 0.3: N4 — охват дашборда (scope=me/family/ids в /api/budget/summary),
/// N5 — справочник банков с идемпотентным сидом.
/// </summary>
public class Task03N4N5Tests
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
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static BudgetController WithUser(AppDbContext db, int userId)
    {
        var controller = new BudgetController(db, new AccountRulesService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    private static decimal GetNumber(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;

    private static async Task<(User Me, User Spouse)> AddFamilyAsync(AppDbContext db)
    {
        var me = new User { Email = "me@schitayka.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var spouse = new User { Email = "spouse@schitayka.ru", Name = "Супруг(а)", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();
        return (me, spouse);
    }

    private static async Task AddTxAsync(AppDbContext db, int userId, int categoryId,
        TxType type, decimal amount, DateOnly date)
    {
        db.Transactions.Add(new Transaction
        {
            UserId = userId, CategoryId = categoryId, Type = type, Amount = amount, Date = date,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Category> AddCategoryAsync(AppDbContext db, int userId, string name)
    {
        var cat = new Category { UserId = userId, Name = name, Type = TxType.Expense, Color = "#007aff" };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        return cat;
    }

    // ---------- N4: охват дашборда ----------

    [Fact]
    public async Task Summary_Me_ByDefault_AggregatesOnlyOwnData()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        var catMe = await AddCategoryAsync(db, me.Id, "Продукты");
        var catSpouse = await AddCategoryAsync(db, spouse.Id, "Продукты");
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 5);
        await AddTxAsync(db, me.Id, catMe.Id, TxType.Expense, 1000m, month);
        await AddTxAsync(db, spouse.Id, catSpouse.Id, TxType.Expense, 5000m, month);

        var result = await WithUser(db, me.Id).Summary();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize((result as OkObjectResult)!.Value));

        Assert.Equal(1000m, (decimal)GetNumber(doc.RootElement, "monthExpense"));
        var scope = doc.RootElement.GetProperty("scope").GetString();
        Assert.Equal("me", scope);
    }

    [Fact]
    public async Task Summary_FamilyScope_AggregatesAllMembersAndMergesSameNamedCategories()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        // Одинаковые категории у обоих: id разные, названия одинаковые — должны объединиться.
        var catMe = await AddCategoryAsync(db, me.Id, "Продукты");
        var catSpouse = await AddCategoryAsync(db, spouse.Id, "Продукты");
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 5);
        await AddTxAsync(db, me.Id, catMe.Id, TxType.Expense, 1000m, month);
        await AddTxAsync(db, spouse.Id, catSpouse.Id, TxType.Expense, 5000m, month);
        await AddTxAsync(db, me.Id, catMe.Id, TxType.Income, 10000m, month);

        var result = await WithUser(db, me.Id).Summary(year: month.Year, month: month.Month, scope: "family");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize((result as OkObjectResult)!.Value));
        var root = doc.RootElement;

        Assert.Equal(6000m, (decimal)GetNumber(root, "monthExpense"));
        Assert.Equal(10000m, (decimal)GetNumber(root, "monthIncome"));
        Assert.Equal("family", root.GetProperty("scope").GetString());

        // Обе операции «Продукты» — одна строка структуры расходов
        var shares = root.GetProperty("expenseShare");
        Assert.Equal(1, shares.GetArrayLength());
        Assert.Equal(6000m, (decimal)GetNumber(shares[0], "total"));

        // Список членов семьи для переключателя
        var users = root.GetProperty("scopeUsers");
        Assert.Equal(2, users.GetArrayLength());
    }

    [Fact]
    public async Task Summary_IdsScope_OnlySelectedMembersAndForeignIdsDropped()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        var outsider = new User { Email = "out@schitayka.ru", Name = "Чужой", PasswordHash = "x:y" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var catSpouse = await AddCategoryAsync(db, spouse.Id, "Транспорт");
        var catOut = await AddCategoryAsync(db, outsider.Id, "Транспорт");
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 5);
        await AddTxAsync(db, spouse.Id, catSpouse.Id, TxType.Expense, 2000m, month);
        await AddTxAsync(db, outsider.Id, catOut.Id, TxType.Expense, 9000m, month);

        // Чужой id отбрасывается — агрегируем только выбранного члена семьи
        var result = await WithUser(db, me.Id)
            .Summary(year: month.Year, month: month.Month, scope: "ids", ids: $"{spouse.Id},{outsider.Id}");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize((result as OkObjectResult)!.Value));
        var root = doc.RootElement;

        Assert.Equal(2000m, (decimal)GetNumber(root, "monthExpense"));
        Assert.Equal("ids", root.GetProperty("scope").GetString());
        var scopeIds = root.GetProperty("scopeIds").EnumerateArray().Select(x => x.GetInt32()).ToList();
        Assert.Equal(new[] { spouse.Id }, scopeIds);
    }

    [Fact]
    public async Task Summary_IdsScopeWithoutValidIds_FallsBackToMe()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        var result = await WithUser(db, me.Id).Summary(scope: "ids", ids: "999,888");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize((result as OkObjectResult)!.Value));

        Assert.Equal("me", doc.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Summary_FamilyScope_TotalBalance_SumsFamilyAccounts()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        db.Accounts.Add(new Account { UserId = me.Id, Name = "Моя карта", Type = AccountType.DebitCard, StartingBalance = 1000m, Balance = 1000m });
        db.Accounts.Add(new Account { UserId = spouse.Id, Name = "Карта супруга", Type = AccountType.DebitCard, StartingBalance = 4000m, Balance = 4000m });
        await db.SaveChangesAsync();

        var result = await WithUser(db, me.Id).Summary(scope: "family");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize((result as OkObjectResult)!.Value));

        Assert.Equal(5000m, (decimal)GetNumber(doc.RootElement, "totalBalance"));
    }

    [Fact]
    public async Task Summary_FamilyScope_SkipsPersonalRecommendations()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        // Цель без срока — личная рекомендация «нет даты» должна появиться только при scope=me
        db.SavingsGoals.Add(new SavingsGoal { UserId = me.Id, Name = "Отпуск", TargetAmount = 100000, CurrentAmount = 0 });
        await db.SaveChangesAsync();

        var meScope = await WithUser(db, me.Id).Summary();
        var familyScope = await WithUser(db, me.Id).Summary(scope: "family");

        using var d1 = JsonDocument.Parse(JsonSerializer.Serialize((meScope as OkObjectResult)!.Value));
        using var d2 = JsonDocument.Parse(JsonSerializer.Serialize((familyScope as OkObjectResult)!.Value));
        var recsMe = d1.RootElement.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("text").GetString()).ToList();
        var recsFamily = d2.RootElement.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("text").GetString()).ToList();

        Assert.Contains(recsMe, t => t!.Contains("нет срока"));
        Assert.DoesNotContain(recsFamily, t => t!.Contains("нет срока"));
    }

    // ---------- N5: справочник банков ----------

    [Fact]
    public void SeedBanks_AddsAllDefaultsAndIsIdempotent()
    {
        var (conn, db) = CreateDbSync();
        try
        {
            SeedBanks.Seed(db);
            SeedBanks.Seed(db); // повторный сид не должен дублировать

            var system = db.Banks.Where(b => b.UserId == null).ToList();
            Assert.Equal(SeedBanks.Defaults.Length, system.Count);
            Assert.All(system, b => Assert.False(string.IsNullOrEmpty(b.BrandColor)));
            Assert.Contains(system, b => b.Code == "tbank" && b.ShortName == "Т");
        }
        finally { conn.Dispose(); db.Dispose(); }
    }

    [Fact]
    public void SeedBanks_BackfillsBrandFieldsOfLegacyRows()
    {
        var (conn, db) = CreateDbSync();
        try
        {
            // Банк, сидированный до N5: без брендовых полей
            db.Banks.Add(new Bank { Name = "Сбербанк", Code = "sber", Country = "RU", IsSystem = true });
            db.SaveChanges();

            SeedBanks.Seed(db);

            var sber = db.Banks.Single(b => b.Code == "sber");
            Assert.Equal("#21A038", sber.BrandColor);
            Assert.Equal("Сбер", sber.ShortName);
            Assert.Equal(SeedBanks.Defaults.Length, db.Banks.Count(b => b.UserId == null));
        }
        finally { conn.Dispose(); db.Dispose(); }
    }

    private static (SqliteConnection Conn, AppDbContext Db) CreateDbSync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    private static BanksController WithBankUser(AppDbContext db, int userId)
    {
        var controller = new BanksController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(userId) },
        };
        return controller;
    }

    [Fact]
    public async Task Banks_CreateOwnBank_AndCannotDuplicate()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, _) = await AddFamilyAsync(db);
        var controller = WithBankUser(db, me.Id);

        var created = await controller.Create(new BanksController.BankDto(0, "Мой местный банк", "", "RU", false, BrandColor: "#123456"));
        var ok = Assert.IsType<OkObjectResult>(created);
        var dto = Assert.IsType<BanksController.BankDto>(ok.Value);
        Assert.False(dto.IsSystem);
        Assert.Equal("#123456", dto.BrandColor);

        // Дубликат запрещён
        var again = await controller.Create(new BanksController.BankDto(0, "Мой местный банк", "", "RU", false));
        Assert.IsType<BadRequestObjectResult>(again);

        // Некорректный цвет нормализуется в null
        var badColor = await controller.Create(new BanksController.BankDto(0, "Ещё банк", "", "RU", false, BrandColor: "red"));
        var ok2 = Assert.IsType<OkObjectResult>(badColor);
        Assert.Null(Assert.IsType<BanksController.BankDto>(ok2.Value).BrandColor);
    }

    [Fact]
    public async Task Banks_CannotEditOrDeleteSystemBank()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, _) = await AddFamilyAsync(db);
        SeedBanks.Seed(db);
        var controller = WithBankUser(db, me.Id);
        var tbank = db.Banks.Single(b => b.Code == "tbank");

        Assert.IsType<BadRequestObjectResult>(await controller.Update(tbank.Id,
            new BanksController.BankDto(tbank.Id, "Хак", "hack", "RU", true)));
        Assert.IsType<BadRequestObjectResult>(await controller.Delete(tbank.Id));

        Assert.Equal("Т-Банк", (await db.Banks.SingleAsync(b => b.Id == tbank.Id)).Name);
    }

    [Fact]
    public async Task Banks_DeleteOwnBank_BlockedWhenReferencedByAccount()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn; using var __ = db;
        var (me, _) = await AddFamilyAsync(db);
        var controller = WithBankUser(db, me.Id);

        var created = await controller.Create(new BanksController.BankDto(0, "Мой банк", "", "RU", false));
        var bank = Assert.IsType<BanksController.BankDto>(Assert.IsType<OkObjectResult>(created).Value);

        db.Accounts.Add(new Account { UserId = me.Id, Name = "Карта", Type = AccountType.DebitCard, BankId = bank.Id });
        await db.SaveChangesAsync();

        var blocked = await controller.Delete(bank.Id);
        Assert.IsType<BadRequestObjectResult>(blocked);

        // После отвязки счёта удаление проходит
        var acc = await db.Accounts.SingleAsync(a => a.BankId == bank.Id);
        acc.BankId = null;
        await db.SaveChangesAsync();
        var deleted = await controller.Delete(bank.Id);
        Assert.IsType<OkObjectResult>(deleted);
        Assert.False(await db.Banks.AnyAsync(b => b.Id == bank.Id));
    }
}
