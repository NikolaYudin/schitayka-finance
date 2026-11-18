using System.Collections;
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
/// Общие категории семьи: один список на всех членов, объединяем одноимённые
/// категории и перенаправляем ссылки на каноническую.
/// </summary>
public class FamilyCategoryTests
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

    private static async Task<(User Me, User Spouse)> AddFamilyAsync(AppDbContext db)
    {
        var me = new User { Email = "me@family.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-9" };
        var spouse = new User { Email = "spouse@family.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-9" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();
        return (me, spouse);
    }

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

    private static CategoriesController Categories(AppDbContext db, int uid)
    {
        var c = new CategoriesController(db);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(uid) },
        };
        return c;
    }

    private static TransactionsController Transactions(AppDbContext db, int uid)
    {
        var c = new TransactionsController(db, new AccountRulesService(db));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PrincipalFor(uid) },
        };
        return c;
    }

    private static List<object> AsList(IActionResult result) =>
        ((IEnumerable)((OkObjectResult)result).Value!).Cast<object>().ToList();

    // ---------- Общий список ----------

    [Fact]
    public async Task CreatedCategory_VisibleToFamilyMember()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        var created = Assert.IsType<OkObjectResult>(await Categories(db, me.Id).Create(
            new CategoriesController.CategoryDto("Ремонт", TxType.Expense, "#ff0000")));
        var id = created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;

        var myList = AsList(await Categories(db, me.Id).List());
        var spouseList = AsList(await Categories(db, spouse.Id).List());

        Assert.Contains(myList, x => x.GetType().GetProperty("Name")!.GetValue(x)!.Equals("Ремонт"));
        Assert.Contains(spouseList, x => x.GetType().GetProperty("Name")!.GetValue(x)!.Equals("Ремонт"));
        Assert.NotNull(await CategoryService.FindAsync(db, spouse.Id, (int)id));
    }

    [Fact]
    public async Task DuplicateAcrossFamily_Rejected()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        await Categories(db, me.Id).Create(new CategoriesController.CategoryDto("Продукты", TxType.Expense, null));

        var result = await Categories(db, spouse.Id).Create(
            new CategoriesController.CategoryDto("Продукты", TxType.Expense, null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RenameBySpouse_VisibleToOwner()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        var created = Assert.IsType<OkObjectResult>(await Categories(db, me.Id).Create(
            new CategoriesController.CategoryDto("Старое", TxType.Expense, null)));
        var id = (int)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;

        var updated = await Categories(db, spouse.Id).Update(id,
            new CategoriesController.CategoryDto("Новое", TxType.Expense, "#00ff00"));

        Assert.IsType<OkObjectResult>(updated);
        var category = await db.Categories.SingleAsync(c => c.Id == id);
        Assert.Equal("Новое", category.Name);
        Assert.Equal("#00ff00", category.Color);
    }

    [Fact]
    public async Task Transaction_CanUseSpouseCategory()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        var created = Assert.IsType<OkObjectResult>(await Categories(db, spouse.Id).Create(
            new CategoriesController.CategoryDto("Общая", TxType.Expense, null)));
        var id = (int)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;
        db.Accounts.Add(new Account
        {
            UserId = me.Id, Name = "Карта", Type = AccountType.DebitCard,
            StartingBalance = 1000m, Balance = 1000m, Color = "#fff",
        });
        await db.SaveChangesAsync();

        var result = await Transactions(db, me.Id).Create(new TransactionsController.TxDto(
            id, TxType.Expense, 200m, new DateOnly(2026, 11, 10), "обед", null));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(id, (await db.Transactions.SingleAsync()).CategoryId);
    }

    [Fact]
    public async Task SystemCategory_CreatedByOne_VisibleToAll()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);

        db.Accounts.Add(new Account
        {
            UserId = me.Id, Name = "Карта", Type = AccountType.DebitCard,
            StartingBalance = 1000m, Balance = 1000m, Color = "#fff",
        });
        await db.SaveChangesAsync();
        var accountId = (await db.Accounts.SingleAsync()).Id;
        var service = new DebtService(db);
        var (_, _, debt) = await service.CreateAsync(me.Id, new DebtService.DebtInput(
            (int)DebtDirection.IOwe, "Банк", "Кредит", 1000m, "RUB", null, null, 0m, null, null, ""));
        Assert.NotNull(debt);
        await service.AddEventAsync(me.Id, debt!.Id, new DebtService.DebtEventInput(
            (int)DebtEventKind.Repayment, 100m, null, "", true, accountId));

        var category = await db.Categories.SingleAsync(c => c.Name == "Долги");
        Assert.NotNull(await CategoryService.FindAsync(db, spouse.Id, category.Id));
    }

    // ---------- Нормализация ----------

    [Fact]
    public async Task Normalize_MergesDuplicatesAndRemapsReferences()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        var mine = new Category { UserId = me.Id, Name = "Продукты", Type = TxType.Expense, Color = "#111" };
        var theirs = new Category { UserId = spouse.Id, Name = "Продукты", Type = TxType.Expense, Color = "#222" };
        db.Categories.AddRange(mine, theirs);
        await db.SaveChangesAsync();
        db.Transactions.Add(new Transaction
        {
            UserId = spouse.Id, CategoryId = theirs.Id, Type = TxType.Expense,
            Amount = 100m, Date = new DateOnly(2026, 11, 10),
        });
        db.BudgetLimits.Add(new BudgetLimit
        {
            UserId = spouse.Id, CategoryId = theirs.Id, Year = 2026, Month = 11, Limit = 500m,
        });
        await db.SaveChangesAsync();
        var staleId = theirs.Id;

        await CategoryService.NormalizeFamilyAsync(db, me.Id);

        var categories = await db.Categories.Where(c => c.Name == "Продукты").ToListAsync();
        Assert.Single(categories);
        Assert.Equal("FAM-9", categories[0].FamilyCode);
        Assert.Equal(categories[0].Id, (await db.Transactions.SingleAsync()).CategoryId);
        Assert.Equal(categories[0].Id, (await db.BudgetLimits.SingleAsync()).CategoryId);
        Assert.False(await db.Categories.AnyAsync(c => c.Id == staleId));
    }

    [Fact]
    public async Task Normalize_IsIdempotentAndKeepsPersonalCategoriesSeparate()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, _) = await AddFamilyAsync(db);
        var personalOwner = new User { Email = "alone@family.ru", Name = "Один", PasswordHash = "x:y" };
        db.Users.Add(personalOwner);
        await db.SaveChangesAsync();
        db.Categories.Add(new Category { UserId = personalOwner.Id, Name = "Продукты", Type = TxType.Expense });
        await db.SaveChangesAsync();

        await CategoryService.NormalizeFamilyAsync(db, me.Id);
        await CategoryService.NormalizeFamilyAsync(db, me.Id);

        Assert.Equal(1, await db.Categories.CountAsync());
        Assert.Null((await db.Categories.SingleAsync()).FamilyCode);
    }

    [Fact]
    public async Task LeaveFamily_CategoriesBecomePersonal()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;
        var (me, spouse) = await AddFamilyAsync(db);
        var shared = new Category
        {
            UserId = me.Id, Name = "Общая", Type = TxType.Expense, FamilyCode = "FAM-9",
        };
        db.Categories.Add(shared);
        await db.SaveChangesAsync();

        await CategoryService.DetachOnLeaveAsync(db, me.Id, "FAM-9");
        me.FamilyCode = null;
        await db.SaveChangesAsync();

        Assert.Null((await db.Categories.SingleAsync()).FamilyCode);
        var accessible = await (await CategoryService.AccessibleAsync(db, me.Id)).ToListAsync();
        Assert.Single(accessible);
        Assert.NotNull(spouse);
    }
}
