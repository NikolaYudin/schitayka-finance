using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Route("api/export")]
[Authorize]
public class ExportController : ControllerBase
{
    private readonly AppDbContext _db;

    public ExportController(AppDbContext db) => _db = db;

    private int Uid => UserAccess.UidOf(User);

    /// <summary>
    /// Полный JSON-профиль пользователя для анализа ИИ-агентом.
    /// Экспортируются только собственные данные: без паролей, JWT, кода семьи
    /// и чужих семейных счетов/операций.
    /// </summary>
    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Uid, ct);
        if (user is null) return Unauthorized(new { error = "Пользователь не найден" });

        var categories = await _db.Categories.AsNoTracking()
            .Where(c => c.UserId == Uid)
            .OrderBy(c => c.Type).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.UserId == Uid)
            .OrderBy(a => a.Type).ThenBy(a => a.Name)
            .ToListAsync(ct);

        var transactions = await _db.Transactions.AsNoTracking()
            .Where(t => t.UserId == Uid)
            .OrderBy(t => t.Date).ThenBy(t => t.Id)
            .ToListAsync(ct);

        var limits = await _db.BudgetLimits.AsNoTracking()
            .Where(l => l.UserId == Uid)
            .OrderBy(l => l.Year).ThenBy(l => l.Month).ThenBy(l => l.CategoryId)
            .ToListAsync(ct);

        var goals = await _db.SavingsGoals.AsNoTracking()
            .Where(g => g.UserId == Uid)
            .OrderBy(g => g.Deadline).ThenBy(g => g.Id)
            .ToListAsync(ct);

        var rules = await _db.AccountRules.AsNoTracking()
            .Where(r => r.UserId == Uid)
            .OrderBy(r => r.Kind).ThenBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var alerts = await _db.AccountAlerts.AsNoTracking()
            .Where(a => a.UserId == Uid && a.ResolvedAt == null && a.DismissedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        var loans = await _db.ReserveLoans.AsNoTracking()
            .Where(l => l.UserId == Uid && l.ReturnedAt == null)
            .OrderBy(l => l.DueDate).ThenBy(l => l.Id)
            .ToListAsync(ct);

        var familyMembersCount = string.IsNullOrEmpty(user.FamilyCode)
            ? 1
            : await _db.Users.AsNoTracking().CountAsync(u => u.FamilyCode == user.FamilyCode, ct);

        var categoriesById = categories.ToDictionary(c => c.Id);
        var accountsById = accounts.ToDictionary(a => a.Id);

        var activeAccounts = accounts.Where(a => !a.IsArchived).ToList();
        var analytics = transactions.Where(t => !t.IsTransfer).ToList();

        var allIncome = analytics.Where(t => t.Type == TxType.Income).Sum(t => t.Amount);
        var allExpense = analytics.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount);

        decimal totalBalance = activeAccounts.Count > 0
            ? activeAccounts.Sum(a => a.Type == AccountType.CreditCard ? -a.Balance : a.Balance)
            : user.StartingBalance + allIncome - allExpense;

        var today = DateTime.Today;
        var currentMonth = analytics
            .Where(t => t.Date.Year == today.Year && t.Date.Month == today.Month)
            .ToList();
        var currentIncome = currentMonth.Where(t => t.Type == TxType.Income).Sum(t => t.Amount);
        var currentExpense = currentMonth.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount);

        var byMonth = analytics
            .GroupBy(t => new { t.Date.Year, t.Date.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var income = g.Where(t => t.Type == TxType.Income).Sum(t => t.Amount);
                var expense = g.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount);
                return new
                {
                    year = g.Key.Year,
                    month = g.Key.Month,
                    income,
                    expense,
                    balance = income - expense,
                    savingsRate = income > 0 ? Math.Round((income - expense) / income * 100, 2) : 0m,
                    transactionsCount = g.Count(),
                };
            })
            .ToList();

        var currentExpenseShare = currentMonth
            .Where(t => t.Type == TxType.Expense)
            .GroupBy(t => t.CategoryId)
            .Select(g =>
            {
                var category = categoriesById.GetValueOrDefault(g.Key);
                return new
                {
                    categoryId = g.Key,
                    categoryName = category?.Name ?? "—",
                    color = category?.Color ?? "#718096",
                    total = g.Sum(t => t.Amount),
                    sharePct = currentExpense > 0
                        ? Math.Round(g.Sum(t => t.Amount) / currentExpense * 100, 2)
                        : 0m,
                };
            })
            .OrderByDescending(x => x.total)
            .ToList();

        var allTimeExpenseShare = analytics
            .Where(t => t.Type == TxType.Expense)
            .GroupBy(t => t.CategoryId)
            .Select(g =>
            {
                var category = categoriesById.GetValueOrDefault(g.Key);
                return new
                {
                    categoryId = g.Key,
                    categoryName = category?.Name ?? "—",
                    color = category?.Color ?? "#718096",
                    total = g.Sum(t => t.Amount),
                    sharePct = allExpense > 0
                        ? Math.Round(g.Sum(t => t.Amount) / allExpense * 100, 2)
                        : 0m,
                    transactionsCount = g.Count(),
                };
            })
            .OrderByDescending(x => x.total)
            .ToList();

        var limitRows = limits.Select(l =>
        {
            var spent = analytics
                .Where(t => t.Date.Year == l.Year && t.Date.Month == l.Month
                            && t.CategoryId == l.CategoryId && t.Type == TxType.Expense)
                .Sum(t => t.Amount);
            return new
            {
                l.Id,
                l.Year,
                l.Month,
                l.CategoryId,
                categoryName = categoriesById.GetValueOrDefault(l.CategoryId)?.Name ?? "—",
                l.Limit,
                spent,
                remaining = l.Limit - spent,
                usagePct = l.Limit > 0 ? Math.Round(spent / l.Limit * 100, 2) : 0m,
                overLimit = spent > l.Limit,
            };
        }).ToList();

        var goalRows = goals.Select(g =>
        {
            decimal? perMonth = null;
            int? monthsLeft = null;
            if (g.Deadline is { } deadline)
            {
                var todayDate = DateOnly.FromDateTime(today);
                var remaining = Math.Max(0, g.TargetAmount - g.CurrentAmount);
                monthsLeft = Math.Max(0,
                    (deadline.Year - todayDate.Year) * 12 + (deadline.Month - todayDate.Month)
                    + (deadline.Day >= todayDate.Day ? 0 : -1) + 1);
                perMonth = monthsLeft > 0 ? Math.Round(remaining / monthsLeft.Value, 0) : remaining;
            }

            return new
            {
                g.Id,
                g.Name,
                g.TargetAmount,
                g.CurrentAmount,
                deadline = g.Deadline?.ToString("yyyy-MM-dd"),
                progress = g.TargetAmount > 0 ? Math.Round(g.CurrentAmount / g.TargetAmount * 100, 2) : 0m,
                monthsLeft,
                perMonth,
                g.CreatedAt,
            };
        }).ToList();

        var activeReserveRules = rules
            .Where(r => r.Kind == AccountRuleKind.EmergencyReserve && r.IsEnabled && r.Threshold != null)
            .ToList();
        var totalNonCredit = activeAccounts.Where(a => a.Type != AccountType.CreditCard).Sum(a => a.Balance);
        var totalLoanTaken = loans.Sum(l => l.Amount);

        var reserveRows = activeReserveRules.Select(r =>
        {
            var account = r.AccountId is { } aid ? activeAccounts.FirstOrDefault(a => a.Id == aid) : null;
            var balance = r.AccountId is null ? totalNonCredit : account?.Balance ?? 0;
            var taken = r.AccountId is { } aid2
                ? loans.Where(l => l.AccountId == aid2).Sum(l => l.Amount)
                : totalLoanTaken;
            var threshold = r.Threshold ?? 0;
            return new
            {
                ruleId = r.Id,
                ruleName = r.Name,
                r.AccountId,
                accountName = account?.Name,
                threshold,
                balance,
                taken,
                free = AccountRulesService.FreeReserve(balance, threshold, taken),
            };
        }).ToList();

        var payload = new
        {
            meta = new
            {
                schemaVersion = 1,
                exportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                application = "СчитайКа",
                scope = "personal",
                currency = "RUB",
                description = "Полный профиль пользователя для анализа ИИ-агентом: счета, операции, категории, лимиты, цели, правила и агрегаты.",
            },
            application = new
            {
                version = AppVersion.Current.Version,
                commit = AppVersion.Current.Commit,
                shortCommit = AppVersion.Current.ShortCommit,
                buildDate = AppVersion.Current.BuildDate?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            user = new
            {
                user.Id,
                user.Name,
                user.Email,
                user.StartingBalance,
                createdAt = user.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                hasFamily = !string.IsNullOrEmpty(user.FamilyCode),
                familyMembersCount,
            },
            summary = new
            {
                currentMonth = new
                {
                    year = today.Year,
                    month = today.Month,
                    income = currentIncome,
                    expense = currentExpense,
                    balance = currentIncome - currentExpense,
                    savingsRate = currentIncome > 0
                        ? Math.Round((currentIncome - currentExpense) / currentIncome * 100, 2)
                        : 0m,
                },
                allTime = new
                {
                    income = allIncome,
                    expense = allExpense,
                    balance = allIncome - allExpense,
                    savingsRate = allIncome > 0
                        ? Math.Round((allIncome - allExpense) / allIncome * 100, 2)
                        : 0m,
                },
                totalBalance,
                cashbackTotal = activeAccounts.Sum(a => a.CashbackAccrued),
                accountsCount = accounts.Count,
                activeAccountsCount = activeAccounts.Count,
                transactionsCount = transactions.Count,
                categoriesCount = categories.Count,
            },
            accounts = accounts.Select(a => new
            {
                a.Id,
                a.Name,
                type = AccountTypeKey(a.Type),
                typeName = AccountTypeNames.Ru(a.Type),
                a.StartingBalance,
                a.Balance,
                a.CreditLimit,
                a.CashbackRate,
                a.CashbackAccrued,
                a.Color,
                a.IsArchived,
                available = a.Type == AccountType.CreditCard && a.CreditLimit > 0
                    ? Math.Max(0, a.CreditLimit - a.Balance)
                    : (a.Type == AccountType.CreditCard ? 0 : a.Balance),
                creditUsagePct = a.Type == AccountType.CreditCard && a.CreditLimit > 0
                    ? Math.Round(a.Balance / a.CreditLimit * 100, 2)
                    : (decimal?)null,
                createdAt = a.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }),
            categories = categories.Select(c => new
            {
                c.Id,
                c.Name,
                type = TxTypeKey(c.Type),
                c.Color,
                c.IsSystem,
                transactionsCount = transactions.Count(t => t.CategoryId == c.Id),
            }),
            transactions = transactions.Select(t => new
            {
                t.Id,
                date = t.Date.ToString("yyyy-MM-dd"),
                type = TxTypeKey(t.Type),
                t.Amount,
                t.CategoryId,
                categoryName = categoriesById.GetValueOrDefault(t.CategoryId)?.Name,
                t.AccountId,
                accountName = t.AccountId is { } aid ? accountsById.GetValueOrDefault(aid)?.Name : null,
                t.Comment,
                t.IsTransfer,
                createdAt = t.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }),
            limits = limitRows,
            goals = goalRows,
            monthly = byMonth,
            expenseShare = new
            {
                currentMonth = currentExpenseShare,
                allTime = allTimeExpenseShare,
            },
            accountRules = rules.Select(r => new
            {
                r.Id,
                r.Name,
                kind = RuleKindKey(r.Kind),
                kindName = RuleKindName(r.Kind),
                r.IsEnabled,
                r.Priority,
                r.AccountId,
                accountName = r.AccountId is { } aid ? accountsById.GetValueOrDefault(aid)?.Name : null,
                txType = r.TxType is { } tx ? TxTypeKey(tx) : null,
                r.MatchCategoryId,
                matchCategoryName = r.MatchCategoryId is { } mcid
                    ? categoriesById.GetValueOrDefault(mcid)?.Name : null,
                r.CommentContains,
                r.AmountMin,
                r.AmountMax,
                r.SetCategoryId,
                setCategoryName = r.SetCategoryId is { } scid
                    ? categoriesById.GetValueOrDefault(scid)?.Name : null,
                r.Threshold,
                r.DefaultReturnDays,
                r.NotifyInApp,
                createdAt = r.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }),
            alerts = alerts.Select(a => new
            {
                a.Id,
                a.AccountId,
                accountName = a.AccountId is { } aid ? accountsById.GetValueOrDefault(aid)?.Name : null,
                a.RuleId,
                a.Kind,
                a.Level,
                a.Message,
                a.BalanceSnapshot,
                createdAt = a.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }),
            reserves = reserveRows,
            reserveLoans = loans.Select(l => new
            {
                l.Id,
                l.AccountId,
                accountName = accountsById.GetValueOrDefault(l.AccountId)?.Name,
                l.RuleId,
                l.Amount,
                takenAt = l.TakenAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                dueDate = l.DueDate.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                l.Comment,
                isOverdue = l.DueDate < DateTime.UtcNow.Date,
            }),
            // Задача 5: доходность счетов (ставка % годовых вместо кэшбэка)
            interest = accounts.Where(a => a.InterestRate > 0).Select(a => new
            {
                a.Id,
                a.Name,
                type = AccountTypeKey(a.Type),
                typeName = AccountTypeNames.Ru(a.Type),
                rate = a.InterestRate,
                mode = (int)a.InterestMode,
                modeName = AccountsController.InterestModeName(a.InterestMode),
                openedAt = a.OpenedAt?.ToString("yyyy-MM-dd"),
                maturesAt = a.MaturesAt?.ToString("yyyy-MM-dd"),
                taxRate = a.InterestTaxRate,
                yearly = SchitayKa.Api.Services.InterestCalculator.YearlySimple(a.Balance, a.InterestRate),
                daily = Math.Round(SchitayKa.Api.Services.InterestCalculator.DailyIncome(a.Balance, a.InterestRate), 2),
            }),
            // Задача 6: мониторинг инвестиций (портфели по банкам)
            investments = await _db.InvestmentPortfolios
                .Where(p => p.UserId == Uid)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    bankName = p.Bank.Name,
                    product = (int)p.Product,
                    strategy = (int)p.Strategy,
                    p.Currency,
                    createdAt = p.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    holdings = p.Holdings.Select(h => new
                    {
                        h.Id,
                        h.Name,
                        h.Ticker,
                        assetType = (int)h.AssetType,
                        h.Quantity,
                        h.AvgPrice,
                        h.CurrentPrice,
                        h.Currency,
                        invested = Math.Round(h.Quantity * (h.AssetType == AssetType.Cash ? 1m : h.AvgPrice), 2),
                        value = Math.Round(h.Quantity * (h.AssetType == AssetType.Cash ? 1m
                            : (h.CurrentPrice > 0 ? h.CurrentPrice : h.AvgPrice)), 2),
                    }),
                })
                .ToListAsync(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        Response.Headers["Cache-Control"] = "no-store";
        var fileName = $"schitayka-profile-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        return File(json, "application/json; charset=utf-8", fileName);
    }

    private static string TxTypeKey(TxType type) => type == TxType.Income ? "income" : "expense";

    private static string AccountTypeKey(AccountType type) => type switch
    {
        AccountType.DebitCard => "debitCard",
        AccountType.CreditCard => "creditCard",
        AccountType.Cash => "cash",
        AccountType.Deposit => "deposit",
        _ => "other",
    };

    private static string RuleKindKey(AccountRuleKind kind) => kind switch
    {
        AccountRuleKind.AutoAssign => "autoAssign",
        AccountRuleKind.MinBalance => "minBalance",
        AccountRuleKind.EmergencyReserve => "emergencyReserve",
        _ => "unknown",
    };

    private static string RuleKindName(AccountRuleKind kind) => kind switch
    {
        AccountRuleKind.AutoAssign => "Автоподстановка",
        AccountRuleKind.MinBalance => "Неснижаемый остаток",
        AccountRuleKind.EmergencyReserve => "Резерв",
        _ => "Неизвестно",
    };
}
