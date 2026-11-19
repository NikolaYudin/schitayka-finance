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
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<Bank> Banks => Set<Bank>();
    public DbSet<InvestmentPortfolio> InvestmentPortfolios => Set<InvestmentPortfolio>();
    public DbSet<InvestmentHolding> InvestmentHoldings => Set<InvestmentHolding>();
    public DbSet<Debt> Debts => Set<Debt>();
    public DbSet<DebtEvent> DebtEvents => Set<DebtEvent>();
    public DbSet<Obligation> Obligations => Set<Obligation>();
    public DbSet<ObligationPayment> ObligationPayments => Set<ObligationPayment>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>().HasIndex(u => u.Email).IsUnique();
        mb.Entity<User>().HasIndex(u => u.YandexId).IsUnique();

        mb.Entity<Category>()
            .HasOne(c => c.User).WithMany(u => u.Categories).HasForeignKey(c => c.UserId);
        mb.Entity<Category>().HasIndex(c => new { c.UserId, c.Name, c.Type }).IsUnique();
        // Поиск общих категорий семьи (после нормализации — одна запись на семью).
        mb.Entity<Category>().HasIndex(c => new { c.FamilyCode, c.Name, c.Type });

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

        // --- Задача 7: переводы между счетами ---
        mb.Entity<Transfer>().HasIndex(t => new { t.UserId, t.Date });
        mb.Entity<Transfer>().HasIndex(t => new { t.FromTxId, t.ToTxId });
        mb.Entity<Transfer>()
            .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Transfer>()
            .HasOne(t => t.FromAccount).WithMany().HasForeignKey(t => t.FromAccountId).OnDelete(DeleteBehavior.Restrict);
        mb.Entity<Transfer>()
            .HasOne(t => t.ToAccount).WithMany().HasForeignKey(t => t.ToAccountId).OnDelete(DeleteBehavior.Restrict);

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

        // --- Задача 2: долги и история событий ---
        mb.Entity<Debt>().HasIndex(d => new { d.UserId, d.Status, d.DueDate });
        mb.Entity<Debt>()
            .HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Debt>()
            .HasOne(d => d.Account).WithMany().HasForeignKey(d => d.AccountId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<DebtEvent>().HasIndex(e => new { e.DebtId, e.Date });
        mb.Entity<DebtEvent>()
            .HasOne(e => e.Debt).WithMany(d => d.Events).HasForeignKey(e => e.DebtId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<DebtEvent>()
            .HasOne(e => e.Transaction).WithMany().HasForeignKey(e => e.TransactionId).OnDelete(DeleteBehavior.SetNull);

        // --- Задача 3: обязательные платежи и факты оплаты ---
        mb.Entity<Obligation>().HasIndex(o => new { o.UserId, o.Status, o.NextPaymentDate });
        mb.Entity<Obligation>()
            .HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Obligation>()
            .HasOne(o => o.Account).WithMany().HasForeignKey(o => o.AccountId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Obligation>()
            .HasOne(o => o.Category).WithMany().HasForeignKey(o => o.CategoryId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Obligation>()
            .HasOne(o => o.Debt).WithMany(d => d.Obligations)
            .HasForeignKey(o => o.DebtId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<ObligationPayment>().HasIndex(p => new { p.ObligationId, p.Date });
        mb.Entity<ObligationPayment>()
            .HasOne(p => p.Obligation).WithMany(o => o.Payments)
            .HasForeignKey(p => p.ObligationId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ObligationPayment>()
            .HasOne(p => p.Transaction).WithMany().HasForeignKey(p => p.TransactionId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<ObligationPayment>()
            .HasOne(p => p.DebtEvent).WithMany().HasForeignKey(p => p.DebtEventId)
            .OnDelete(DeleteBehavior.SetNull);

        // --- Задача 5: календарь событий ---
        mb.Entity<CalendarEvent>().HasIndex(e => new { e.UserId, e.Date });
        mb.Entity<CalendarEvent>()
            .HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CalendarEvent>()
            .HasOne(e => e.Debt).WithMany().HasForeignKey(e => e.DebtId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CalendarEvent>()
            .HasOne(e => e.Obligation).WithMany().HasForeignKey(e => e.ObligationId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<CalendarEvent>()
            .HasOne(e => e.Account).WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.SetNull);

        mb.Entity<AuditLog>().HasIndex(a => new { a.UserId, a.CreatedAt });
        mb.Entity<AuditLog>()
            .HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";          // для локальных; для яндекс — fake-уникальный
    public string PasswordHash { get; set; } = "";   // пусто => вход только через Яндекс
    public int PasswordVersion { get; set; }         // инвалидация старых JWT после смены пароля
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
    // Общая категория семьи: UserId — владелец-создатель, но видят и редактируют
    // все члены семьи с этим кодом. null — личная категория без семьи.
    public string? FamilyCode { get; set; }
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

    // --- Привязка счёта к банку (дерево банков) ---
    public int? BankId { get; set; }                 // банк, в котором открыт счёт; null — не привязан
    public Bank? Bank { get; set; }

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

// ─── Задача 7. Переводы между своими счетами ──────────────────────────────

/// <summary>
/// Перевод между своими счетами — одна бизнес-сущность с двумя операциями:
/// расход на счёте-отправителе и доход на счёте-получателе (обе с IsTransfer).
/// </summary>
public class Transfer
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int FromAccountId { get; set; }
    public Account FromAccount { get; set; } = null!;
    public int ToAccountId { get; set; }
    public Account ToAccount { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public string Comment { get; set; } = "";
    public int? FromTxId { get; set; }      // операция «расход» на отправителе
    public int? ToTxId { get; set; }        // операция «доход» на получателе
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

// Источник последней цены позиции (пункт 1.0 плана).
public enum QuoteSource
{
    Manual = 0,   // цену ввёл/обновил пользователь
    MoexIss = 1,  // MOEX ISS (публичный API Мосбиржи)
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

    // Задача 0.3 (N5): брендовые поля для карточек счетов (N6).
    public string? ShortName { get; set; }            // «Сбер», «Т-Банк» — для монограммы и плиток
    public string? BrandColor { get; set; }           // брендовый цвет, например "#21A038"
    public string? LogoUrl { get; set; }              // ссылка на логотип; null — рисуем монограмму
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

    // --- Котировки (пункт 1.0 плана) ---
    public QuoteSource? QuoteSource { get; set; }      // null — цена ещё не обновлялась автоматически
    public DateTime? PriceUpdatedAt { get; set; }      // время последнего обновления цены
    public string? QuoteError { get; set; }            // последняя ошибка источника (для карточки)
}

// ─── Задача 2. Долги ───────────────────────────────────────────────────────

/// <summary>Направление долга: пользователь должен или должны пользователю.</summary>
public enum DebtDirection
{
    IOwe = 0,      // я должен
    OwedToMe = 1,  // мне должны
}

/// <summary>Хранимый статус долга. «Просрочен» вычисляется по сроку и остатку.</summary>
public enum DebtStatus
{
    Active = 0,
    Closed = 1,
}

/// <summary>Тип события в истории долга.</summary>
public enum DebtEventKind
{
    Issued = 0,          // возникновение долга (создаётся автоматически)
    Repayment = 1,       // возврат части или всего долга
    InterestAccrued = 2, // начислены проценты (увеличивают остаток)
    InterestPaid = 3,    // оплачены проценты (уменьшают остаток)
    Adjustment = 4,      // корректировка: сумма со знаком (+ увеличить, − уменьшить)
    WriteOff = 5,        // списание безнадёжного долга
}

/// <summary>
/// Денежное обязательство (Задача 2): кому и сколько должен пользователь либо кто должен ему.
/// Долги учитываются отдельно от доходов/расходов бюджета (решение 18.11.2026).
/// </summary>
public class Debt
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public DebtDirection Direction { get; set; }
    public string Counterparty { get; set; } = "";     // человек или организация
    public string Name { get; set; } = "";
    public decimal PrincipalAmount { get; set; }       // основная сумма
    public decimal RemainingAmount { get; set; }       // текущий остаток (пересчитывается из событий)
    public string Currency { get; set; } = "RUB";

    public DateOnly IssuedAt { get; set; }
    public DateOnly? DueDate { get; set; }             // срок возврата (опционально)
    public decimal InterestRate { get; set; }          // ставка, % (0 — без процентов)
    public string? Terms { get; set; }                 // условия/комментарий по договору

    public int? AccountId { get; set; }                // счёт по умолчанию для операций
    public Account? Account { get; set; }
    public string Note { get; set; } = "";
    public DebtStatus Status { get; set; } = DebtStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DebtEvent> Events { get; set; } = [];
    public List<Obligation> Obligations { get; set; } = []; // связанный график платежей (Задача 4)
}

/// <summary>Событие истории долга: возврат, проценты, корректировка, списание.</summary>
public class DebtEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int DebtId { get; set; }
    public Debt Debt { get; set; } = null!;
    public DebtEventKind Kind { get; set; }
    public decimal Amount { get; set; }                // для Adjustment допускается отрицательное значение
    public DateOnly Date { get; set; }
    public string Note { get; set; } = "";
    public int? TransactionId { get; set; }            // операция, если событие её создало
    public Transaction? Transaction { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Задача 3. Обязательные платежи ─────────────────────────────────────────

/// <summary>Периодичность обязательного платежа.</summary>
public enum ObligationPeriodicity
{
    OneTime = 0,    // разово
    Monthly = 1,    // ежемесячно
    Quarterly = 2,  // ежеквартально
    Yearly = 3,     // ежегодно
    Custom = 4,     // произвольно: интервал в днях
}

/// <summary>Состояние обязательного платежа. «Просрочен» вычисляется по дате.</summary>
public enum ObligationStatus
{
    Active = 0,
    Paused = 1,
    Completed = 2,
}

/// <summary>
/// Обязательный платёж (Задача 3): регулярные траты, не обязательно связанные с долгом —
/// коммуналка, подписки, налоги, кредит. Сам платёж — это план и не влияет на бюджет;
/// движение денег появляется только если при оплате создана операция (решение 18.11.2026).
/// </summary>
public class Obligation
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string Name { get; set; } = "";
    public string Payee { get; set; } = "";              // получатель: человек или организация
    public int? CategoryId { get; set; }                 // категория для операции при оплате
    public Category? Category { get; set; }
    public decimal Amount { get; set; }                  // ожидаемая/фиксированная сумма

    public ObligationPeriodicity Periodicity { get; set; } = ObligationPeriodicity.Monthly;
    public DateOnly NextPaymentDate { get; set; }        // дата следующего платежа (может быть в прошлом = просрочка)
    public int? PaymentDay { get; set; }                 // день месяца для периодичных платежей (1–31)
    public int? IntervalDays { get; set; }               // интервал для Periodicity.Custom

    public int? AccountId { get; set; }                  // счёт списания по умолчанию
    public Account? Account { get; set; }
    public bool Remind { get; set; }                     // напоминание о платеже
    public int? DebtId { get; set; }                     // связь с долгом (задача 4), опционально
    public Debt? Debt { get; set; }
    public string Note { get; set; } = "";

    public ObligationStatus Status { get; set; } = ObligationStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ObligationPayment> Payments { get; set; } = [];
}

/// <summary>Факт оплаты обязательного платежа.</summary>
public class ObligationPayment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int ObligationId { get; set; }
    public Obligation Obligation { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }                   // фактическая дата оплаты
    public DateOnly PlannedDate { get; set; }            // дата по графику, которая оплачена
    public string Note { get; set; } = "";
    public int? TransactionId { get; set; }              // операция, если она создавалась при оплате
    public Transaction? Transaction { get; set; }
    public int? DebtEventId { get; set; }                // событие возврата по связанному долгу (Задача 4)
    public DebtEvent? DebtEvent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Задача 5. Календарь событий ─────────────────────────────────────────────

/// <summary>Тип пользовательского события календаря.</summary>
public enum CalendarEventType
{
    Event = 0,       // событие
    Reminder = 1,    // напоминание
    Meeting = 2,     // встреча
    Other = 3,       // прочее
}

/// <summary>
/// Пользовательское событие/напоминание календаря (Задача 5).
/// Остальные события календарь собирает из долгов, платежей, вкладов и начислений.
/// </summary>
public class CalendarEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string Title { get; set; } = "";
    public DateOnly Date { get; set; }
    public string? Time { get; set; }                    // "HH:mm", опционально
    public CalendarEventType Type { get; set; } = CalendarEventType.Event;
    public decimal? Amount { get; set; }
    public bool Remind { get; set; }
    public string Note { get; set; } = "";

    public int? DebtId { get; set; }
    public Debt? Debt { get; set; }
    public int? ObligationId { get; set; }
    public Obligation? Obligation { get; set; }
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Аудит чувствительных действий: экспорт/импорт данных и смена пароля.
/// В журнал никогда не попадают пароли, токены, содержимое чеков и изображения.
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
