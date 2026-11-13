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
    public DbSet<AccountRule> AccountRules => Set<AccountRule>();
    public DbSet<AccountAlert> AccountAlerts => Set<AccountAlert>();
    public DbSet<ReserveLoan> ReserveLoans => Set<ReserveLoan>();
    public DbSet<InterestAccrual> InterestAccruals => Set<InterestAccrual>();
    public DbSet<Bank> Banks => Set<Bank>();
    public DbSet<InvestmentPortfolio> InvestmentPortfolios => Set<InvestmentPortfolio>();
    public DbSet<InvestmentHolding> InvestmentHoldings => Set<InvestmentHolding>();

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
        mb.Entity<BudgetLimit>().HasIndex(l => new { l.UserId, l.CategoryId, l.Year, l.Month }).IsUnique();

        mb.Entity<Account>()
            .HasOne(a => a.User).WithMany(u => u.Accounts).HasForeignKey(a => a.UserId);
        mb.Entity<Account>().HasIndex(a => new { a.UserId, a.Name }).IsUnique();

        mb.Entity<Transaction>()
            .HasOne(t => t.Account).WithMany(a => a.Transactions).HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.SetNull);

        mb.Entity<FamilyInvitation>().HasIndex(i => i.Code).IsUnique();

        // --- Правила счетов, уведомления и займы из резерва ---
        mb.Entity<AccountRule>().HasIndex(r => new { r.UserId, r.Priority });
        mb.Entity<AccountRule>()
            .HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AccountRule>()
            .HasOne(r => r.Account).WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AccountRule>()
            .HasOne<Category>().WithMany().HasForeignKey(r => r.MatchCategoryId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<AccountRule>()
            .HasOne<Category>().WithMany().HasForeignKey(r => r.SetCategoryId).OnDelete(DeleteBehavior.SetNull);

        mb.Entity<AccountAlert>().HasIndex(a => new { a.UserId, a.AccountId, a.ResolvedAt });
        mb.Entity<AccountAlert>()
            .HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AccountAlert>()
            .HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<AccountAlert>()
            .HasOne<AccountRule>().WithMany().HasForeignKey(a => a.RuleId).OnDelete(DeleteBehavior.SetNull);

        mb.Entity<ReserveLoan>().HasIndex(l => new { l.UserId, l.ReturnedAt, l.DueDate });
        mb.Entity<ReserveLoan>()
            .HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ReserveLoan>()
            .HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ReserveLoan>()
            .HasOne<AccountRule>().WithMany().HasForeignKey(l => l.RuleId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<ReserveLoan>()
            .HasOne<Transaction>().WithMany().HasForeignKey(l => l.TransactionId).OnDelete(DeleteBehavior.SetNull);

        // --- Задача 5: начисления процентов ---
        mb.Entity<InterestAccrual>().HasIndex(a => new { a.UserId, a.AccountId, a.From });
        mb.Entity<InterestAccrual>()
            .HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<InterestAccrual>()
            .HasOne(a => a.Account).WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);

        // --- Задача 6: банки, портфели и активы ---
        mb.Entity<Bank>().HasIndex(b => new { b.UserId, b.Code });
        mb.Entity<InvestmentPortfolio>().HasIndex(p => new { p.UserId, p.Name });
        mb.Entity<InvestmentPortfolio>()
            .HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<InvestmentPortfolio>()
            .HasOne(p => p.Bank).WithMany().HasForeignKey(p => p.BankId).OnDelete(DeleteBehavior.Restrict);
        mb.Entity<InvestmentHolding>().HasIndex(h => new { h.PortfolioId, h.Name });
        mb.Entity<InvestmentHolding>()
            .HasOne(h => h.User).WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<InvestmentHolding>()
            .HasOne(h => h.Portfolio).WithMany(p => p.Holdings)
            .HasForeignKey(h => h.PortfolioId).OnDelete(DeleteBehavior.Cascade);
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
    // Перевод между своими счетами (пара расход+доход): не считается доходом/расходом
    // в аналитике и рекомендациях, но влияет на балансы счетов.
    public bool IsTransfer { get; set; }
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

    // --- Доходность (Задача 5): для вкладов и накопительных счетов ---
    public decimal InterestRate { get; set; }        // ставка % годовых (вместо кэшбэка для вкладов)
    public InterestAccrualMode InterestMode { get; set; } = InterestAccrualMode.None;
    public DateTime? OpenedAt { get; set; }          // дата открытия (вклада)
    public DateTime? MaturesAt { get; set; }         // дата окончания (вклада)
    public decimal InterestTaxRate { get; set; } = 13m; // НДФЛ, %

    public string Color { get; set; } = "#2b6cb0";
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Transaction> Transactions { get; set; } = [];
}

// Режим начисления процентов на счёте (Задача 5).
public enum InterestAccrualMode
{
    None = 0,           // ставка не задана
    DailyToBalance = 1, // накопительный счёт: % ежедневно на остаток, с капитализацией
    MonthlyPayout = 2,  // накопительный счёт: % ежедневно на остаток, выплата раз в месяц
    EndOfTerm = 3,      // вклад: % на сумму за срок, выплата в конце срока
}

// Журнал начислений процентов по счёту (Задача 5).
public class InterestAccrual
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Days { get; set; }
    public decimal Gross { get; set; }     // начислено брутто
    public decimal Tax { get; set; }       // НДФЛ
    public decimal Net { get; set; }       // на руки
    public bool IsPaid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

// ---- Правила счетов ----
public enum AccountRuleKind { AutoAssign = 0, MinBalance = 1, EmergencyReserve = 2 }

public class AccountRule
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public AccountRuleKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;          // меньше = раньше (для AutoAssign)

    // Для AutoAssign и MinBalance — обязателен; для EmergencyReserve null = общий резерв.
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    // --- AutoAssign ---
    public TxType? TxType { get; set; }
    public int? MatchCategoryId { get; set; }
    public string? CommentContains { get; set; }
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public int? SetCategoryId { get; set; }           // опционально: автокатегория

    // --- MinBalance / EmergencyReserve ---
    public decimal? Threshold { get; set; }            // минимальный остаток / размер резерва
    public int? DefaultReturnDays { get; set; }        // срок возврата займа из резерва
    public bool NotifyInApp { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Уведомление по правилу счёта: неснижаемый остаток или нарушение резерва.
public class AccountAlert
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? AccountId { get; set; }                // null — уведомление по общему резерву
    public int? RuleId { get; set; }
    public string Kind { get; set; } = "";             // MinBalance | ReserveBroken | ReserveOverdue
    public string Level { get; set; } = "warn";        // warn | danger
    public string Message { get; set; } = "";
    public decimal BalanceSnapshot { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public DateTime? DismissedAt { get; set; }
}

// Заём из резерва: деньги потрачены, пользователю нужно вернуть их в резерв.
public class ReserveLoan
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int AccountId { get; set; }
    public int? RuleId { get; set; }
    public decimal Amount { get; set; }
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;
    public DateTime DueDate { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public int? TransactionId { get; set; }
    public string Comment { get; set; } = "";
}

// ─── Задача 6. Мониторинг инвестиций и Инвесткопилки ───────────────────────

// Продукт банка, в котором открыт инвестиционный счёт.
public enum BankProduct
{
    Savings = 0,      // накопительный счёт
    Deposit = 1,      // вклад
    Broker = 2,       // брокерский счёт
    IIS = 3,          // ИИС
    MoneyBox = 4,     // Инвесткопилка (Т-Банк): копилка + автопилот в фонды
}

// Стратегия Инвесткопилки Т-Банка (tbank.ru/invest/moneybox).
public enum MoneyBoxStrategy
{
    AlwaysPlus = 0,        // «Всегда в плюсе» — фонд «Денежный рынок», доход ≈ ставки ЦБ
    Balanced = 1,          // «Сбалансированная» — фонд «Вечный портфель»
    StableCompanies = 2,   // «Стабильные компании» — фонд «Крупнейшие компании РФ»
}

public enum AssetType
{
    Cash = 0,    // денежная часть копилки
    Fund = 1,    // фонд / ETF
    Share = 2,   // акция
    Bond = 3,    // облигация
    Gold = 4,    // золото
    Other = 9,
}

// Банк, в котором открыт счёт. UserId = null — системный справочник.
public class Bank
{
    public int Id { get; set; }
    public int? UserId { get; set; }                 // null — общий справочник
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";            // короткий код, например "tbank"
    public string Country { get; set; } = "RU";
    public bool IsSystem { get; set; } = true;
}

// Портфель инвестиций: счёт в конкретном банке.
public class InvestmentPortfolio
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public int BankId { get; set; }
    public Bank Bank { get; set; } = null!;
    public BankProduct Product { get; set; }
    public MoneyBoxStrategy Strategy { get; set; } = MoneyBoxStrategy.AlwaysPlus;
    public string Currency { get; set; } = "RUB";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<InvestmentHolding> Holdings { get; set; } = [];
}

// Позиция портфеля: бумага, фонд или деньги копилки.
public class InvestmentHolding
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int PortfolioId { get; set; }
    public InvestmentPortfolio Portfolio { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Ticker { get; set; } = "";           // ISIN / тикер / просто метка
    public AssetType AssetType { get; set; }
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }              // средняя цена покупки
    public decimal CurrentPrice { get; set; }          // текущая цена (0 — не задана)
    public string Currency { get; set; } = "RUB";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
