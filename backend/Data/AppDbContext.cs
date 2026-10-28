using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<BudgetLimit> BudgetLimits => Set<BudgetLimit>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FamilyInvitation> FamilyInvitations => Set<FamilyInvitation>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>().HasIndex(u => u.Email).IsUnique();
        mb.Entity<User>().HasIndex(u => u.YandexId).IsUnique();

        mb.Entity<Category>()
            .HasOne(c => c.User).WithMany(u => u.Categories).HasForeignKey(c => c.UserId);
        mb.Entity<Category>().HasIndex(c => new { c.UserId, c.Name, c.Type }).IsUnique();

        mb.Entity<Transaction>()
            .HasOne(t => t.User).WithMany(u => u.Transactions).HasForeignKey(t => t.UserId);
        mb.Entity<Transaction>()
            .HasOne(t => t.Category).WithMany(c => c.Transactions).HasForeignKey(t => t.CategoryId);
        mb.Entity<Transaction>().HasIndex(t => new { t.UserId, t.Date });

        mb.Entity<SavingsGoal>()
            .HasOne(g => g.User).WithMany(u => u.SavingsGoals).HasForeignKey(g => g.UserId);

        mb.Entity<BudgetLimit>()
            .HasOne(l => l.User).WithMany(u => u.BudgetLimits).HasForeignKey(l => l.UserId);
        mb.Entity<BudgetLimit>()
            .HasOne(l => l.Category).WithMany(c => c.Limits).HasForeignKey(l => l.CategoryId);
        mb.Entity<BudgetLimit>().HasIndex(l => new { l.UserId, l.CategoryId, l.Month }).IsUnique();

        mb.Entity<Account>()
            .HasOne(a => a.User).WithMany(u => u.Accounts).HasForeignKey(a => a.UserId);
        mb.Entity<Account>().HasIndex(a => new { a.UserId, a.Name }).IsUnique();

        mb.Entity<Transaction>()
            .HasOne(t => t.Account).WithMany(a => a.Transactions).HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.SetNull);

        mb.Entity<FamilyInvitation>().HasIndex(i => i.Code).IsUnique();
    }
}

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";          // для локальных; для яндекс — fake-уникальный
    public string PasswordHash { get; set; } = "";   // пусто => вход только через Яндекс
    public string Name { get; set; } = "";
    public string? YandexId { get; set; }
    public decimal StartingBalance { get; set; }     // стартовый баланс («сколько на счетах сейчас»)
    public string? FamilyCode { get; set; }           // код семейной группы (приглашения)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Category> Categories { get; set; } = [];
    public List<Transaction> Transactions { get; set; } = [];
    public List<SavingsGoal> SavingsGoals { get; set; } = [];
    public List<BudgetLimit> BudgetLimits { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
}

public enum TxType { Income = 0, Expense = 1 }

// Типы счетов: дебетовая карта, кредитная карта, наличные, вклад/копилка, прочее
public enum AccountType { DebitCard = 0, CreditCard = 1, Cash = 2, Deposit = 3, Other = 4 }

public static class AccountTypeNames
{
    public static string Ru(AccountType t) => t switch
    {
        AccountType.DebitCard => "Дебетовая карта",
        AccountType.CreditCard => "Кредитная карта",
        AccountType.Cash => "Наличные",
        AccountType.Deposit => "Вклад / копилка",
        _ => "Прочее",
    };
}

public class Category
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public TxType Type { get; set; }
    public string Color { get; set; } = "#2b6cb0";
    public bool IsSystem { get; set; }               // стартовая категория — нельзя удалить
    public List<Transaction> Transactions { get; set; } = [];
    public List<BudgetLimit> Limits { get; set; } = [];
}

public class Transaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public TxType Type { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public string Comment { get; set; } = "";
    public int? AccountId { get; set; }              // счёт, к которому относится операция
    public Account? Account { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Счёт пользователя: дебетовый/кредитный/наличные/вклад.
// Для кредитных «баланс» хранится как сумма долга (положительное число).
public class Account
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";           // имя — любое («Тинькофф Black», «Кэшбэлка», «Конверт»)
    public AccountType Type { get; set; }
    public decimal StartingBalance { get; set; }     // стартовый остаток счёта (задаётся при создании)
    public decimal Balance { get; set; }             // текущий остаток (долг для кредитных), пересчитывается из операций
    public decimal CreditLimit { get; set; }         // лимит кредитной карты (0 — не задан)
    public decimal CashbackRate { get; set; }        // ставка кэшбэка, %
    public decimal CashbackAccrued { get; set; }     // накопленный кэшбэк
    public string Color { get; set; } = "#2b6cb0";
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Transaction> Transactions { get; set; } = [];
}

// Пригласительный код для семьи: по коду другой пользователь присоединяется к FamilyCode создателя
public class FamilyInvitation
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }             // кто пригласил
    public string Code { get; set; } = "";           // короткий код для ввода
    public string? Email { get; set; }               // приглашённый по email (опционально)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public int? AcceptedByUserId { get; set; }
}

public class SavingsGoal
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public DateOnly? Deadline { get; set; }          // к какой копии копим
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Лимит расходов на категорию за конкретный месяц (для анализа бюджета)
public class BudgetLimit
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int Year { get; set; }
    public int Month { get; set; }                   // 1..12
    public decimal Limit { get; set; }
}
