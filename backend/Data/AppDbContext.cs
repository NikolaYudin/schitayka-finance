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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Category> Categories { get; set; } = [];
    public List<Transaction> Transactions { get; set; } = [];
    public List<SavingsGoal> SavingsGoals { get; set; } = [];
    public List<BudgetLimit> BudgetLimits { get; set; } = [];
}

public enum TxType { Income = 0, Expense = 1 }

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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
