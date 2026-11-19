using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

// B5: сервис месячной сводки должен давать те же результаты, что давал контроллер,
// и сохранять JSON-контракт для frontend/src/api.ts (Summary).
public class BudgetSummaryServiceTests
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

    private static BudgetSummaryService Service(AppDbContext db) => new(db, new AccountRulesService(db));

    private static Category Category(AppDbContext db, int userId, string name, TxType type)
    {
        var cat = new Category { UserId = userId, Name = name, Type = type };
        db.Categories.Add(cat);
        db.SaveChanges();
        return cat;
    }

    private static void AddTx(AppDbContext db, int userId, int categoryId, TxType type,
        decimal amount, DateOnly date, bool isTransfer = false)
    {
        db.Transactions.Add(new Transaction
        {
            UserId = userId, CategoryId = categoryId, Type = type, Amount = amount,
            Date = date, IsTransfer = isTransfer,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetAsync_MonthIncomeExpense_ExcludesTransfers_AndComputesTotals()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;

        var user = new User
        {
            Email = "test@schitayka.ru", Name = "Тест", PasswordHash = "x:y",
            StartingBalance = 500m,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var salary = Category(db, user.Id, "Зарплата", TxType.Income);
        var products = Category(db, user.Id, "Продукты", TxType.Expense);
        var account = new Account
        {
            UserId = user.Id, Name = "Основная", Type = AccountType.DebitCard,
            StartingBalance = 80000, Balance = 80000,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 11, 5);
        AddTx(db, user.Id, salary.Id, TxType.Income, 100000m, date);
        AddTx(db, user.Id, products.Id, TxType.Expense, 20000m, date);
        // Пара переводов между своими счетами не должна попасть ни в доходы, ни в расходы.
        AddTx(db, user.Id, products.Id, TxType.Income, 50000m, date, isTransfer: true);
        AddTx(db, user.Id, products.Id, TxType.Expense, 50000m, date, isTransfer: true);
        AddTx(db, user.Id, salary.Id, TxType.Income, 1000m, new DateOnly(2026, 10, 5));

        var result = await Service(db).GetAsync(user.Id, 2026, 11, null, null);

        Assert.Equal(2026, result.Year);
        Assert.Equal(11, result.Month);
        Assert.Equal("me", result.Scope);
        Assert.Equal("Только я", result.ScopeLabel);
        Assert.Equal(500m, result.StartingBalance);
        Assert.Equal(100000m, result.MonthIncome);
        Assert.Equal(20000m, result.MonthExpense);
        Assert.Equal(80000m, result.Balance);
        Assert.Equal(80m, result.SavingsRate);
        Assert.Equal(80000m, result.TotalBalance); // баланс счёта, а не стартовый баланс
        Assert.Single(result.ByMonth.Where(m => m.Month == 11 && m.Income == 100000m && m.Expense == 20000m));
        Assert.Single(result.ByMonth.Where(m => m.Month == 10 && m.Income == 1000m));
        var share = Assert.Single(result.ExpenseShare);
        Assert.Equal("Продукты", share.Name);
        Assert.Equal(20000m, share.Total);
    }

    [Fact]
    public async Task GetAsync_FamilyScope_AggregatesAllMembersAndUsesFamilyAccountsForTotal()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;

        var me = new User { Email = "me@t.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var spouse = new User { Email = "sp@t.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();

        // Одинаковые названия, разные владельцы: cat ищется только по Id (последний фикс).
        var catMe = Category(db, me.Id, "Продукты", TxType.Expense);
        var catSpouse = Category(db, spouse.Id, "Продукты", TxType.Expense);
        db.Accounts.AddRange(
            new Account { UserId = me.Id, Name = "Моя карта", Type = AccountType.DebitCard, Balance = 1000m },
            new Account { UserId = spouse.Id, Name = "Карта супруга", Type = AccountType.DebitCard, Balance = 4000m });
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 11, 5);
        AddTx(db, me.Id, catMe.Id, TxType.Expense, 1000m, date);
        AddTx(db, spouse.Id, catSpouse.Id, TxType.Expense, 5000m, date);
        AddTx(db, me.Id, catMe.Id, TxType.Income, 10000m, date);

        var result = await Service(db).GetAsync(me.Id, 2026, 11, "family", null);

        Assert.Equal("family", result.Scope);
        Assert.Equal("Вся семья", result.ScopeLabel);
        Assert.Equal(6000m, result.MonthExpense);
        Assert.Equal(10000m, result.MonthIncome);
        Assert.Equal(5000m, result.TotalBalance);
        Assert.Equal(2, result.ScopeIds.Count);
        Assert.Equal(2, result.ScopeUsers!.Count);
        Assert.Contains(result.ScopeUsers, u => u.Id == me.Id && u.IsMe);
        var share = Assert.Single(result.ExpenseShare);
        Assert.Equal(6000m, share.Total); // категории с одинаковым названием объединены
        var familyAccount = Assert.Single(result.FamilyAccounts);
        Assert.Equal(spouse.Id, familyAccount.OwnerId);
        Assert.Equal(4000m, result.FamilyTotalBalance);
        Assert.Single(result.Accounts); // личные счета — только мои
    }

    [Fact]
    public async Task GetAsync_IdsScope_SelectsOnlyAllowedMembers_AndFallsBackToMe()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;

        var me = new User { Email = "me@t.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var spouse = new User { Email = "sp@t.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var outsider = new User { Email = "out@t.ru", Name = "Чужой", PasswordHash = "x:y" };
        db.Users.AddRange(me, spouse, outsider);
        await db.SaveChangesAsync();

        var catSpouse = Category(db, spouse.Id, "Транспорт", TxType.Expense);
        var catOut = Category(db, outsider.Id, "Транспорт", TxType.Expense);
        var date = new DateOnly(2026, 11, 5);
        AddTx(db, spouse.Id, catSpouse.Id, TxType.Expense, 2000m, date);
        AddTx(db, outsider.Id, catOut.Id, TxType.Expense, 9000m, date);

        var selected = await Service(db).GetAsync(me.Id, 2026, 11, "ids", $"{spouse.Id},{outsider.Id}");

        Assert.Equal("ids", selected.Scope);
        Assert.Equal("Выбранные", selected.ScopeLabel);
        Assert.Equal(new[] { spouse.Id }, selected.ScopeIds);
        Assert.Equal(2000m, selected.MonthExpense); // чужой id отброшен

        var fallback = await Service(db).GetAsync(me.Id, 2026, 11, "ids", "999,888");
        Assert.Equal("me", fallback.Scope);
        Assert.Equal(new[] { me.Id }, fallback.ScopeIds);
    }

    [Fact]
    public async Task GetAsync_Recommendations_PersonalOnly_AndCashbackComesFirst()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;

        var me = new User { Email = "me@t.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var spouse = new User { Email = "sp@t.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();
        var cat = Category(db, me.Id, "Продукты", TxType.Expense);
        db.Accounts.Add(new Account
        {
            UserId = me.Id, Name = "Кэшбэлка", Type = AccountType.DebitCard,
            Balance = 50000m, CashbackAccrued = 150m,
        });
        db.SavingsGoals.Add(new SavingsGoal
        {
            UserId = me.Id, Name = "Отпуск", TargetAmount = 100000m, CurrentAmount = 10000m,
        });
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 11, 5);
        AddTx(db, me.Id, cat.Id, TxType.Income, 100000m, date);
        AddTx(db, me.Id, cat.Id, TxType.Expense, 20000m, date);

        var personal = await Service(db).GetAsync(me.Id, 2026, 11, "me", null);
        Assert.Equal("good", personal.Recommendations[0].Level);
        Assert.Contains("кэшбэк", personal.Recommendations[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(personal.Recommendations, r => r.Text.Contains("нет срока"));
        Assert.Contains(personal.Recommendations, r => r.Text.Contains("Отлично"));

        var family = await Service(db).GetAsync(me.Id, 2026, 11, "family", null);
        Assert.Contains(family.Recommendations, r => r.Text.Contains("кэшбэк", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(family.Recommendations, r => r.Text.Contains("нет срока"));

        // Превышение расходов над доходами — отдельная danger-рекомендация.
        var expense = await db.Transactions.FirstAsync(t => t.Type == TxType.Expense);
        expense.Amount = 120000m;
        await db.SaveChangesAsync();
        var overspend = await Service(db).GetAsync(me.Id, 2026, 11, "me", null);
        Assert.Contains(overspend.Recommendations, r => r.Level == "danger" && r.Text.Contains("превышают доходы"));
    }

    [Fact]
    public async Task GetAsync_SerializedJson_HasExactlyTheSummaryContract()
    {
        var (conn, db) = await CreateDbAsync();
        using var _ = conn;
        await using var __ = db;

        var me = new User { Email = "me@t.ru", Name = "Я", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        var spouse = new User { Email = "sp@t.ru", Name = "Супруг", PasswordHash = "x:y", FamilyCode = "FAM-1" };
        db.Users.AddRange(me, spouse);
        await db.SaveChangesAsync();
        var cat = Category(db, me.Id, "Продукты", TxType.Expense);
        var account = new Account
        {
            UserId = me.Id, Name = "Карта", Type = AccountType.DebitCard,
            Balance = 10000m,
        };
        var spouseAccount = new Account
        {
            UserId = spouse.Id, Name = "Карта супруга", Type = AccountType.DebitCard,
            Balance = 2000m,
        };
        db.Accounts.AddRange(account, spouseAccount);
        await db.SaveChangesAsync(); // Id счетов нужны для правил и займа ниже
        db.BudgetLimits.Add(new BudgetLimit
        {
            UserId = me.Id, CategoryId = cat.Id, Year = 2026, Month = 11, Limit = 5000m,
        });
        db.SavingsGoals.Add(new SavingsGoal
        {
            UserId = me.Id, Name = "Цель", TargetAmount = 100000m, CurrentAmount = 10000m,
            Deadline = new DateOnly(2027, 1, 1),
        });
        var rule = new AccountRule
        {
            UserId = me.Id, Name = "Подушка", Kind = AccountRuleKind.EmergencyReserve,
            AccountId = account.Id, Threshold = 100m,
        };
        db.AccountRules.Add(rule);
        await db.SaveChangesAsync();
        db.AccountAlerts.Add(new AccountAlert
        {
            UserId = me.Id, AccountId = account.Id, Kind = "Test", Level = "warn",
            Message = "Проверка контракта",
        });
        db.ReserveLoans.Add(new ReserveLoan
        {
            UserId = me.Id, AccountId = account.Id, RuleId = rule.Id, Amount = 50m,
            DueDate = DateTime.UtcNow.Date.AddDays(5),
        });
        AddTx(db, me.Id, cat.Id, TxType.Income, 10000m, new DateOnly(2026, 11, 5));
        AddTx(db, me.Id, cat.Id, TxType.Expense, 2000m, new DateOnly(2026, 11, 6));

        var result = await Service(db).GetAsync(me.Id, 2026, 11, "me", null);

        // Прямая сериализация без политики camelCase: атрибуты имён должны сохранить контракт.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = doc.RootElement;
        AssertPropertyNames(root,
            "year", "month", "scope", "scopeIds", "scopeLabel", "scopeUserIds", "scopeUsers",
            "startingBalance", "balance", "monthIncome", "monthExpense", "totalBalance", "savingsRate",
            "byMonth", "expenseShare", "limits", "goals", "accounts", "familyAccounts",
            "familyTotalBalance", "cashbackTotal", "recommendations", "alerts", "reserveLoans",
            "reserves", "reserveTaken");

        AssertPropertyNames(root.GetProperty("scopeUsers")[0], "id", "name", "isMe");
        AssertPropertyNames(root.GetProperty("byMonth")[0], "year", "month", "income", "expense");
        AssertPropertyNames(root.GetProperty("expenseShare")[0], "categoryId", "name", "color", "total");
        AssertPropertyNames(root.GetProperty("limits")[0], "id", "categoryId", "limit", "name", "color", "spent");
        AssertPropertyNames(root.GetProperty("goals")[0],
            "id", "name", "targetAmount", "currentAmount", "deadline", "progress", "monthsLeft", "perMonth");
        AssertPropertyNames(root.GetProperty("accounts")[0], "id", "name", "type", "typeName", "balance",
            "creditLimit", "cashbackRate", "cashbackAccrued", "color", "bankShortName", "bankColor");
        AssertPropertyNames(root.GetProperty("familyAccounts")[0], "id", "name", "type", "typeName", "balance",
            "creditLimit", "cashbackRate", "cashbackAccrued", "color", "ownerId", "ownerName",
            "bankShortName", "bankColor");
        AssertPropertyNames(root.GetProperty("recommendations")[0], "level", "icon", "text");
        AssertPropertyNames(root.GetProperty("alerts")[0], "id", "accountId", "ruleId", "kind", "level",
            "message", "balanceSnapshot", "createdAt", "accountName");
        AssertPropertyNames(root.GetProperty("reserveLoans")[0], "id", "accountId", "ruleId", "amount",
            "takenAt", "dueDate", "comment", "accountName", "isOverdue");
        AssertPropertyNames(root.GetProperty("reserves")[0], "ruleId", "ruleName", "accountId",
            "accountName", "threshold", "balance", "taken", "free");
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }
}
