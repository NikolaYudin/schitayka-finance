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

    // B15: в память попадают только поля, реально участвующие в сопоставлении.
    internal sealed record AssignmentRuleCondition(
        int Id, string Name, int? AccountId, int? SetCategoryId,
        TxType? TxType, int? MatchCategoryId, string? CommentContains,
        decimal? AmountMin, decimal? AmountMax);

    // Первое подходящее включённое правило по приоритету (меньше — раньше).
    // Условия объединяются через И; пустые условия игнорируются.
    public async Task<RuleResult?> MatchAssignmentAsync(int userId, RuleCandidate candidate)
    {
        var activeAccountIds = await _db.Accounts.AsNoTracking()
            .Where(a => a.UserId == userId && !a.IsArchived)
            .Select(a => a.Id)
            .ToListAsync();
        if (activeAccountIds.Count == 0) return null;

        // B15: правила без счёта или с архивным счётом матчингом всё равно отбрасываются —
        // отфильтровываем их в SQL и не тащим полные сущности в память.
        var rules = await _db.AccountRules.AsNoTracking()
            .Where(r => r.UserId == userId && r.Kind == AccountRuleKind.AutoAssign && r.IsEnabled
                        && r.AccountId != null && activeAccountIds.Contains(r.AccountId.Value))
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .Select(r => new AssignmentRuleCondition(
                r.Id, r.Name, r.AccountId, r.SetCategoryId,
                r.TxType, r.MatchCategoryId, r.CommentContains, r.AmountMin, r.AmountMax))
            .ToListAsync();
        return MatchConditions(rules, activeAccountIds.ToHashSet(), candidate);
    }

    // Чистое сопоставление без обращения к БД — используется и для bulk-apply.
    public static RuleResult? MatchRule(IEnumerable<AccountRule> rules, HashSet<int> activeAccountIds,
        RuleCandidate candidate) =>
        MatchConditions(rules.Select(r => new AssignmentRuleCondition(
            r.Id, r.Name, r.AccountId, r.SetCategoryId,
            r.TxType, r.MatchCategoryId, r.CommentContains, r.AmountMin, r.AmountMax)),
            activeAccountIds, candidate);

    private static RuleResult? MatchConditions(IEnumerable<AssignmentRuleCondition> rules,
        HashSet<int> activeAccountIds, RuleCandidate candidate)
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

    /// <summary>Параметры ретроактивного применения правил (B13).</summary>
    public record ApplyOptions(
        bool DryRun = true,
        bool Overwrite = false,
        DateOnly? From = null,
        DateOnly? To = null,
        int? AccountId = null,
        int? CategoryId = null);

    /// <summary>Итог ретроактивного применения: сколько операций и что именно изменено.</summary>
    public record ApplyResult(
        int Matched, int ChangedAccounts, int ChangedCategories, IReadOnlyList<object> Samples);

    /// <summary>
    /// Применяет включённые правила автоподстановки к существующим операциям.
    /// Логика вынесена из контроллера, чтобы UI, ручной запуск и будущие задачи
    /// использовали один и тот же путь. dryRun ничего не сохраняет.
    /// </summary>
    public async Task<ApplyResult> ApplyToExistingAsync(
        int userId, ApplyOptions options, CancellationToken ct = default)
    {
        // B15: только условия сопоставления, без лишних колонок правил.
        var rules = await _db.AccountRules.AsNoTracking()
            .Where(r => r.UserId == userId && r.Kind == AccountRuleKind.AutoAssign && r.IsEnabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .Select(r => new AssignmentRuleCondition(
                r.Id, r.Name, r.AccountId, r.SetCategoryId,
                r.TxType, r.MatchCategoryId, r.CommentContains, r.AmountMin, r.AmountMax))
            .ToListAsync(ct);
        if (rules.Count == 0)
            return new ApplyResult(0, 0, 0, Array.Empty<object>());

        var activeAccountIds = (await _db.Accounts.AsNoTracking()
            .Where(a => a.UserId == userId && !a.IsArchived)
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var accessibleCategories = await CategoryService.AccessibleAsync(_db, userId);
        var validCategoryIds = (await accessibleCategories
            .Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        var query = _db.Transactions.Where(t => t.UserId == userId);
        if (!options.Overwrite) query = query.Where(t => t.AccountId == null);
        if (options.From is { } from) query = query.Where(t => t.Date >= from);
        if (options.To is { } to) query = query.Where(t => t.Date <= to);
        if (options.AccountId is { } accountId) query = query.Where(t => t.AccountId == accountId);
        if (options.CategoryId is { } categoryId) query = query.Where(t => t.CategoryId == categoryId);

        var txs = await query.OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync(ct);

        var matched = 0;
        var changedAccounts = 0;
        var changedCategories = 0;
        var samples = new List<object>();

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = options.DryRun
            ? null
            : await _db.Database.BeginTransactionAsync(ct);
        foreach (var tx in txs)
        {
            var result = MatchConditions(rules, activeAccountIds,
                new RuleCandidate(tx.Type, tx.Amount, tx.Comment, tx.CategoryId));
            if (result is null) continue;
            matched++;

            if (samples.Count < 20)
                samples.Add(new
                {
                    tx.Id, tx.Date, tx.Comment, ruleName = result.RuleName,
                    accountId = result.AccountId, categoryId = result.SetCategoryId,
                });

            if (options.DryRun) continue;
            if (result.AccountId is { } newAccount && tx.AccountId != newAccount)
            {
                tx.AccountId = newAccount;
                changedAccounts++;
            }
            if (result.SetCategoryId is { } newCategory
                && validCategoryIds.Contains(newCategory) && tx.CategoryId != newCategory)
            {
                tx.CategoryId = newCategory;
                changedCategories++;
            }
        }

        if (!options.DryRun && matched > 0)
        {
            await _db.SaveChangesAsync(ct);
            await AccountBalanceService.RecalcAsync(_db,
                _db.Accounts.Where(a => a.UserId == userId), ct);
            await EvaluateAllAsync(userId);
            await transaction!.CommitAsync(ct);
        }

        return new ApplyResult(matched, changedAccounts, changedCategories, samples);
    }

    // Полная проверка: минимумы, резервы, просрочки займов.
    public async Task EvaluateAllAsync(int userId)
    {
        // B15: правилам и займам нужны лишь отдельные поля — полные сущности больше
        // не выгружаются; уведомления грузятся целиком (они изменяются).
        var rules = await _db.AccountRules.AsNoTracking()
            .Where(r => r.UserId == userId && r.IsEnabled)
            .Select(r => new { r.Id, r.Kind, r.AccountId, r.Threshold })
            .ToListAsync();
        // Счета грузим отслеживаемыми: баланс мог быть изменён в памяти вызывающим
        // до вызова проверки, и старое поведение учитывало это через identity map.
        var accounts = await _db.Accounts.Where(a => a.UserId == userId).ToListAsync();
        var activeAccounts = accounts.Where(a => !a.IsArchived).ToDictionary(a => a.Id);
        var loans = await _db.ReserveLoans.AsNoTracking()
            .Where(l => l.UserId == userId && l.ReturnedAt == null)
            .Select(l => new { l.AccountId, l.RuleId, l.Amount, l.DueDate })
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
