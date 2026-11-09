using SchitayKa.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Services;

// Движок правил счетов: автоподстановка, неснижаемый остаток,
// резерв на экстренные нужды и займы из резерва.
public class AccountRulesService
{
    private readonly AppDbContext _db;
    public AccountRulesService(AppDbContext db) { _db = db; }

    public record RuleCandidate(TxType Type, decimal Amount, string? Comment, int? CategoryId);
    public record RuleResult(int RuleId, string RuleName, int? AccountId, int? SetCategoryId);

    // Первое подходящее включённое правило по приоритету (меньше — раньше).
    // Условия объединяются через И; пустые условия игнорируются.
    public async Task<RuleResult?> MatchAssignmentAsync(int userId, RuleCandidate candidate)
    {
        var rules = await _db.AccountRules.AsNoTracking()
            .Where(r => r.UserId == userId && r.Kind == AccountRuleKind.AutoAssign && r.IsEnabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync();
        if (rules.Count == 0) return null;

        var activeAccountIds = await _db.Accounts
            .Where(a => a.UserId == userId && !a.IsArchived)
            .Select(a => a.Id)
            .ToListAsync();
        return MatchRule(rules, activeAccountIds.ToHashSet(), candidate);
    }

    // Чистое сопоставление без обращения к БД — используется и для bulk-apply.
    public static RuleResult? MatchRule(IEnumerable<AccountRule> rules, HashSet<int> activeAccountIds,
        RuleCandidate candidate)
    {
        var comment = (candidate.Comment ?? "").Trim().ToLowerInvariant();
        foreach (var r in rules)
        {
            if (r.AccountId is null || !activeAccountIds.Contains(r.AccountId.Value)) continue; // архивные счета не используем
            if (r.TxType is { } type && type != candidate.Type) continue;
            if (r.MatchCategoryId is { } matchCat && matchCat != candidate.CategoryId) continue;
            if (!string.IsNullOrWhiteSpace(r.CommentContains)
                && !comment.Contains(r.CommentContains.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                continue;
            if (r.AmountMin is { } min && candidate.Amount < min) continue;
            if (r.AmountMax is { } max && candidate.Amount > max) continue;

            return new RuleResult(r.Id, r.Name, r.AccountId, r.SetCategoryId);
        }
        return null;
    }

    public static decimal FreeReserve(decimal balance, decimal threshold, decimal taken) =>
        Math.Max(0, Math.Min(balance, threshold) - taken);

    // Полная проверка: минимумы, резервы, просрочки займов.
    public async Task EvaluateAllAsync(int userId)
    {
        var rules = await _db.AccountRules
            .Where(r => r.UserId == userId && r.IsEnabled)
            .ToListAsync();
        var accounts = await _db.Accounts.Where(a => a.UserId == userId).ToListAsync();
        var activeAccounts = accounts.Where(a => !a.IsArchived).ToDictionary(a => a.Id);
        var loans = await _db.ReserveLoans
            .Where(l => l.UserId == userId && l.ReturnedAt == null)
            .ToListAsync();
        var activeAlerts = await _db.AccountAlerts
            .Where(a => a.UserId == userId && a.ResolvedAt == null)
            .ToListAsync();

        var loansByAccount = loans.GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var totalLoans = loans.Sum(l => l.Amount);

        // ---------- 0.2. Неснижаемый остаток ----------
        var violatedMin = new HashSet<int>();
        foreach (var rule in rules.Where(r => r.Kind == AccountRuleKind.MinBalance
                                              && r.AccountId is not null && r.Threshold is not null))
        {
            if (!activeAccounts.TryGetValue(rule.AccountId!.Value, out var acc)) continue;
            var threshold = rule.Threshold!.Value;
            var violated = acc.Type == AccountType.CreditCard
                ? acc.Balance > threshold
                : acc.Balance < threshold;
            if (!violated) continue;

            violatedMin.Add(rule.Id);
            var message = acc.Type == AccountType.CreditCard
                ? $"Долг по счёту «{acc.Name}» — {acc.Balance:0} ₽, это выше допустимого {threshold:0} ₽."
                : $"На счёте «{acc.Name}» остаток {acc.Balance:0} ₽ ниже минимального {threshold:0} ₽.";
            UpsertAlert(activeAlerts, userId, acc.Id, rule.Id, "MinBalance", true,
                "warn", message, acc.Balance);
        }
        foreach (var alert in activeAlerts.Where(a => a.Kind == "MinBalance"
                                                      && (a.RuleId is null || !violatedMin.Contains(a.RuleId.Value))))
            alert.ResolvedAt = DateTime.UtcNow;

        // ---------- 0.3. Резерв по конкретному счёту ----------
        var violatedReserve = new HashSet<int>();
        foreach (var rule in rules.Where(r => r.Kind == AccountRuleKind.EmergencyReserve
                                              && r.AccountId is not null && r.Threshold is not null))
        {
            if (!activeAccounts.TryGetValue(rule.AccountId!.Value, out var acc)) continue;
            if (acc.Type == AccountType.CreditCard) continue; // резерв только для некредитных счетов

            var threshold = rule.Threshold!.Value;
            var taken = loansByAccount.GetValueOrDefault(acc.Id);
            if (acc.Balance >= threshold) continue;

            violatedReserve.Add(rule.Id);
            var free = FreeReserve(acc.Balance, threshold, taken);
            var message = $"Резерв на счёте «{acc.Name}» затронут: баланс {acc.Balance:0} ₽ " +
                          $"при резерве {threshold:0} ₽. Свободно {free:0} ₽, взято {taken:0} ₽.";
            UpsertAlert(activeAlerts, userId, acc.Id, rule.Id, "ReserveBroken", true,
                "warn", message, acc.Balance);
        }
        foreach (var alert in activeAlerts.Where(a => a.Kind == "ReserveBroken" && a.AccountId is not null
                                                      && (a.RuleId is null || !violatedReserve.Contains(a.RuleId.Value))))
            alert.ResolvedAt = DateTime.UtcNow;

        // ---------- 0.3. Общий резерв ----------
        var totalNonCredit = activeAccounts.Values
            .Where(a => a.Type != AccountType.CreditCard)
            .Sum(a => a.Balance);
        var violatedGlobal = new HashSet<int>();
        foreach (var rule in rules.Where(r => r.Kind == AccountRuleKind.EmergencyReserve
                                              && r.AccountId is null && r.Threshold is not null))
        {
            var threshold = rule.Threshold!.Value;
            if (totalNonCredit >= threshold) continue;

            violatedGlobal.Add(rule.Id);
            var free = FreeReserve(totalNonCredit, threshold, totalLoans);
            UpsertAlert(activeAlerts, userId, null, rule.Id, "ReserveBroken", true, "warn",
                $"Общий резерв затронут: на некредитных счетах {totalNonCredit:0} ₽ " +
                $"при резерве {threshold:0} ₽. Свободно {free:0} ₽, взято {totalLoans:0} ₽.",
                totalNonCredit);
        }
        foreach (var alert in activeAlerts.Where(a => a.Kind == "ReserveBroken" && a.AccountId is null
                                                      && (a.RuleId is null || !violatedGlobal.Contains(a.RuleId.Value))))
            alert.ResolvedAt = DateTime.UtcNow;

        // ---------- 0.3. Просроченные займы ----------
        var today = DateTime.UtcNow.Date;
        var overdueGroups = loans
            .Where(l => l.DueDate.Date < today)
            .GroupBy(l => new { l.RuleId, l.AccountId })
            .ToList();
        var overdueKeys = overdueGroups
            .Select(g => (RuleId: (int?)g.Key.RuleId, AccountId: (int?)g.Key.AccountId))
            .ToHashSet();

        foreach (var g in overdueGroups)
        {
            var firstDue = g.Min(l => l.DueDate);
            var total = g.Sum(l => l.Amount);
            var accountName = activeAccounts.TryGetValue(g.Key.AccountId, out var acc) ? acc.Name : "счёт";
            UpsertAlert(activeAlerts, userId, g.Key.AccountId, g.Key.RuleId, "ReserveOverdue", true, "danger",
                $"Заём из резерва по счёту «{accountName}» просрочен: {total:0} ₽, срок был {firstDue:dd.MM.yyyy}.",
                total);
        }
        foreach (var alert in activeAlerts.Where(a => a.Kind == "ReserveOverdue"
                                                      && !overdueKeys.Contains((a.RuleId, a.AccountId))))
            alert.ResolvedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // После изменения баланса конкретного счёта проверяем всё состояние:
    // правила общие, поэтому полный проход даёт консистентные уведомления.
    public Task EvaluateAccountAsync(int userId, int? accountId) => EvaluateAllAsync(userId);
    public Task EvaluateReserveAsync(int userId) => EvaluateAllAsync(userId);

    private void UpsertAlert(List<AccountAlert> activeAlerts, int userId,
        int? accountId, int? ruleId, string kind, bool violated,
        string level, string message, decimal snapshot)
    {
        // Активное уведомление = ResolvedAt == null. Скрытое пользователем
        // (DismissedAt != null) не пересоздаём, пока проблема не уйдёт.
        var alert = activeAlerts.FirstOrDefault(a =>
            a.AccountId == accountId && a.RuleId == ruleId && a.Kind == kind);

        if (violated)
        {
            if (alert is null)
            {
                alert = new AccountAlert
                {
                    UserId = userId,
                    AccountId = accountId,
                    RuleId = ruleId,
                    Kind = kind,
                    Level = level,
                    Message = message,
                    BalanceSnapshot = snapshot,
                };
                activeAlerts.Add(alert);
                _db.AccountAlerts.Add(alert);
            }
            else if (alert.DismissedAt is null)
            {
                alert.Level = level;
                alert.Message = message;
                alert.BalanceSnapshot = snapshot;
            }
        }
        else if (alert is not null)
        {
            alert.ResolvedAt = DateTime.UtcNow;
        }
    }

    public async Task<ReserveLoan> TakeReserveLoanAsync(int userId, int? ruleId, int accountId,
        decimal amount, DateTime dueDate, int? transactionId = null, string comment = "")
    {
        if (amount <= 0) throw new ArgumentException("Сумма займа должна быть больше нуля.");
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId && !a.IsArchived)
            ?? throw new ArgumentException("Счёт не найден или находится в архиве.");

        AccountRule? rule;
        if (ruleId is { } rid)
        {
            rule = await _db.AccountRules.FirstOrDefaultAsync(r =>
                r.Id == rid && r.UserId == userId && r.Kind == AccountRuleKind.EmergencyReserve);
            if (rule is null) throw new ArgumentException("Правило резерва не найдено.");
        }
        else
        {
            rule = await _db.AccountRules
                .Where(r => r.UserId == userId && r.Kind == AccountRuleKind.EmergencyReserve
                            && r.IsEnabled && r.Threshold != null
                            && (r.AccountId == accountId || r.AccountId == null))
                .OrderByDescending(r => r.AccountId == accountId)
                .ThenBy(r => r.Id)
                .FirstOrDefaultAsync();
            if (rule is null) throw new ArgumentException("Резерв не настроен — создайте правило резерва.");
        }

        if (dueDate == default)
            dueDate = DateTime.UtcNow.AddDays(rule.DefaultReturnDays is > 0 and <= 365 ? rule.DefaultReturnDays.Value : 30);

        var loan = new ReserveLoan
        {
            UserId = userId,
            AccountId = account.Id,
            RuleId = rule.Id,
            Amount = amount,
            DueDate = dueDate.Date,
            TransactionId = transactionId,
            Comment = comment.Trim(),
        };
        _db.ReserveLoans.Add(loan);
        await _db.SaveChangesAsync();
        await EvaluateAllAsync(userId);
        return loan;
    }

    public async Task ReturnReserveLoanAsync(int userId, int loanId)
    {
        var loan = await _db.ReserveLoans.FirstOrDefaultAsync(l => l.Id == loanId && l.UserId == userId)
                   ?? throw new ArgumentException("Заём не найден.");
        if (loan.ReturnedAt is null)
        {
            loan.ReturnedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await EvaluateAllAsync(userId);
        }
    }
}
