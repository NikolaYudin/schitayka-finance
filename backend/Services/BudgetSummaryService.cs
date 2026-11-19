using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

// Типизированные модели ответа /api/budget/summary.
// Имена JSON зафиксированы атрибутами: так контракт совпадает и со старым
// анонимным объектом, и с прямым JsonSerializer.Serialize без политики camelCase
// (её используют тесты). Ни лишних, ни недостающих полей быть не должно.

/// <summary>Сводка личного бюджета (ранее — анонимный объект в BudgetController).</summary>
public sealed record BudgetSummaryResult(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scopeIds")] List<int> ScopeIds,
    [property: JsonPropertyName("scopeLabel")] string ScopeLabel,
    [property: JsonPropertyName("scopeUserIds")] List<int> ScopeUserIds,
    [property: JsonPropertyName("scopeUsers")] List<BudgetScopeUserRow>? ScopeUsers,
    [property: JsonPropertyName("startingBalance")] decimal StartingBalance,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("monthIncome")] decimal MonthIncome,
    [property: JsonPropertyName("monthExpense")] decimal MonthExpense,
    [property: JsonPropertyName("totalBalance")] decimal TotalBalance,
    [property: JsonPropertyName("savingsRate")] decimal SavingsRate,
    [property: JsonPropertyName("byMonth")] List<BudgetMonthRow> ByMonth,
    [property: JsonPropertyName("expenseShare")] List<BudgetExpenseShareRow> ExpenseShare,
    [property: JsonPropertyName("limits")] List<BudgetLimitRow> Limits,
    [property: JsonPropertyName("goals")] List<BudgetGoalRow> Goals,
    [property: JsonPropertyName("accounts")] List<BudgetAccountRow> Accounts,
    [property: JsonPropertyName("familyAccounts")] List<BudgetFamilyAccountRow> FamilyAccounts,
    [property: JsonPropertyName("familyTotalBalance")] decimal FamilyTotalBalance,
    [property: JsonPropertyName("cashbackTotal")] decimal CashbackTotal,
    [property: JsonPropertyName("recommendations")] List<BudgetRecommendationRow> Recommendations,
    [property: JsonPropertyName("alerts")] List<BudgetAlertRow> Alerts,
    [property: JsonPropertyName("reserveLoans")] List<BudgetReserveLoanRow> ReserveLoans,
    [property: JsonPropertyName("reserves")] List<BudgetReserveRow> Reserves,
    [property: JsonPropertyName("reserveTaken")] decimal ReserveTaken);

/// <summary>Член семьи для переключателя охвата.</summary>
public sealed record BudgetScopeUserRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isMe")] bool IsMe);

/// <summary>Доходы и расходы по месяцу.</summary>
public sealed record BudgetMonthRow(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("income")] decimal Income,
    [property: JsonPropertyName("expense")] decimal Expense);

/// <summary>Структура расходов по категориям.</summary>
public sealed record BudgetExpenseShareRow(
    [property: JsonPropertyName("categoryId")] int CategoryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("total")] decimal Total);

/// <summary>Лимит категории с фактом расходов.</summary>
public sealed record BudgetLimitRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("categoryId")] int CategoryId,
    [property: JsonPropertyName("limit")] decimal Limit,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("spent")] decimal Spent);

/// <summary>Цель накоплений с расчётом плана.</summary>
public sealed record BudgetGoalRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("targetAmount")] decimal TargetAmount,
    [property: JsonPropertyName("currentAmount")] decimal CurrentAmount,
    [property: JsonPropertyName("deadline")] DateOnly? Deadline,
    [property: JsonPropertyName("progress")] decimal Progress,
    [property: JsonPropertyName("monthsLeft")] int? MonthsLeft,
    [property: JsonPropertyName("perMonth")] decimal? PerMonth);

/// <summary>Собственный счёт в сводке.</summary>
public sealed record BudgetAccountRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] AccountType Type,
    [property: JsonPropertyName("typeName")] string TypeName,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("creditLimit")] decimal CreditLimit,
    [property: JsonPropertyName("cashbackRate")] decimal CashbackRate,
    [property: JsonPropertyName("cashbackAccrued")] decimal CashbackAccrued,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("bankShortName")] string? BankShortName,
    [property: JsonPropertyName("bankColor")] string? BankColor);

/// <summary>Счёт другого члена семьи: виден, но в личный бюджет не входит.</summary>
public sealed record BudgetFamilyAccountRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] AccountType Type,
    [property: JsonPropertyName("typeName")] string TypeName,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("creditLimit")] decimal CreditLimit,
    [property: JsonPropertyName("cashbackRate")] decimal CashbackRate,
    [property: JsonPropertyName("cashbackAccrued")] decimal CashbackAccrued,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("ownerId")] int OwnerId,
    [property: JsonPropertyName("ownerName")] string? OwnerName,
    [property: JsonPropertyName("bankShortName")] string? BankShortName,
    [property: JsonPropertyName("bankColor")] string? BankColor);

/// <summary>Персональная рекомендация.</summary>
public sealed record BudgetRecommendationRow(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Активное уведомление по счёту/резерву.</summary>
public sealed record BudgetAlertRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("accountId")] int? AccountId,
    [property: JsonPropertyName("ruleId")] int? RuleId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("balanceSnapshot")] decimal BalanceSnapshot,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("accountName")] string? AccountName);

/// <summary>Непогашенный заём из резерва.</summary>
public sealed record BudgetReserveLoanRow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("accountId")] int AccountId,
    [property: JsonPropertyName("ruleId")] int? RuleId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("takenAt")] DateTime TakenAt,
    [property: JsonPropertyName("dueDate")] DateTime DueDate,
    [property: JsonPropertyName("comment")] string Comment,
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("isOverdue")] bool IsOverdue);

/// <summary>Состояние резерва по правилу.</summary>
public sealed record BudgetReserveRow(
    [property: JsonPropertyName("ruleId")] int RuleId,
    [property: JsonPropertyName("ruleName")] string RuleName,
    [property: JsonPropertyName("accountId")] int? AccountId,
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("threshold")] decimal Threshold,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("taken")] decimal Taken,
    [property: JsonPropertyName("free")] decimal Free);

/// <summary>
/// Вся логика месячной сводки: агрегаты, охват семьи, рекомендации.
/// Вынесена из BudgetController без изменения поведения и JSON-контракта.
/// </summary>
public sealed class BudgetSummaryService
{
    private readonly AppDbContext _db;
    private readonly AccountRulesService _rules;

    public BudgetSummaryService(AppDbContext db, AccountRulesService rules)
    {
        _db = db;
        _rules = rules;
    }

    // B15: компактная карточка банка для соединения со счетами в памяти.
    private sealed record BankBrand(string? ShortName, string? BrandColor);

    /// <summary>
    /// Сводка за месяц. Личные инструменты (лимиты, цели, уведомления, резервы)
    /// считаются по uid; доходы/расходы — по выбранному охвату (me/family/ids).
    /// </summary>
    public async Task<BudgetSummaryResult> GetAsync(int uid, int year, int month,
        string? scope, string? ids, CancellationToken ct = default)
    {
        // Обновляем уведомления по правилам счетов перед отдачей сводки:
        // срок займа мог наступить без новых операций.
        // Вызов оставлен как есть: поведение (включая SaveChanges в сервисе правил) не меняем.
        await _rules.EvaluateAllAsync(uid);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);

        // ---------- Охват агрегатов (N4) ----------
        var familyIds = await FamilyAccess.ManageableUserIdsAsync(_db, uid);
        var (effectiveScope, scopeUserIds) = await FamilyAccess.ResolveScopeAsync(_db, scope, ids, uid);

        var txs = await _db.Transactions.Where(t => scopeUserIds.Contains(t.UserId)).ToListAsync(ct);
        var cats = await _db.Categories.Where(c => scopeUserIds.Contains(c.UserId)).ToListAsync(ct);
        var limits = await _db.BudgetLimits
            .Where(l => l.UserId == uid && l.Year == year && l.Month == month).ToListAsync(ct);
        var accounts = await _db.Accounts.Where(a => a.UserId == uid && !a.IsArchived).ToListAsync(ct);

        // Счета семьи: видны всем членам группы, но не влияют на личный бюджет
        // (доходы/расходы считаются только по собственным операциям).
        // B15: раньше на каждый счёт уходили коррелированные подзапросы за владельцем
        // и банком. Теперь все справочники выбираются пакетно и соединяются в памяти.
        var familyAccountsRaw = await _db.Accounts
            .Where(a => familyIds.Contains(a.UserId) && a.UserId != uid && !a.IsArchived)
            .OrderBy(a => a.UserId).ThenBy(a => a.Name)
            .Select(a => new
            {
                a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
                a.Balance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color,
                a.BankId,
                ownerId = a.UserId,
            })
            .ToListAsync(ct);

        var alertsRaw = await _db.AccountAlerts
            .Where(a => a.UserId == uid && a.ResolvedAt == null && a.DismissedAt == null)
            .OrderBy(a => a.Level == "danger" ? 0 : 1).ThenByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.AccountId, a.RuleId, a.Kind, a.Level, a.Message, a.BalanceSnapshot, a.CreatedAt,
            })
            .ToListAsync(ct);

        var reserveLoansRaw = await _db.ReserveLoans
            .Where(l => l.UserId == uid && l.ReturnedAt == null)
            .OrderBy(l => l.DueDate).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.AccountId, l.RuleId, l.Amount, l.TakenAt, l.DueDate, l.Comment,
            })
            .ToListAsync(ct);

        // B15: по одному IN-запросу на каждый справочник вместо подзапроса на строку.
        var ownerIds = familyAccountsRaw.Select(a => a.ownerId).Distinct().ToList();
        var owners = ownerIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Name })
                .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        var bankIds = accounts.Where(a => a.BankId.HasValue).Select(a => a.BankId!.Value)
            .Concat(familyAccountsRaw.Where(a => a.BankId.HasValue).Select(a => a.BankId!.Value))
            .Distinct().ToList();
        var banks = bankIds.Count == 0
            ? new Dictionary<int, BankBrand>()
            : await _db.Banks.AsNoTracking().Where(b => bankIds.Contains(b.Id))
                .Select(b => new { b.Id, b.ShortName, b.BrandColor })
                .ToDictionaryAsync(b => b.Id, b => new BankBrand(b.ShortName, b.BrandColor), ct);

        var namedAccountIds = alertsRaw.Where(a => a.AccountId.HasValue).Select(a => a.AccountId!.Value)
            .Concat(reserveLoansRaw.Select(l => l.AccountId))
            .Distinct().ToList();
        var accountNames = namedAccountIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Accounts.AsNoTracking().Where(a => namedAccountIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name })
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        BankBrand? BankOf(int? bankId) =>
            bankId is { } id && banks.TryGetValue(id, out var bank) ? bank : null;

        var familyAccounts = familyAccountsRaw.Select(a =>
        {
            var bank = BankOf(a.BankId);
            return new BudgetFamilyAccountRow(
                a.Id, a.Name, a.Type, a.typeName, a.Balance, a.CreditLimit, a.CashbackRate,
                a.CashbackAccrued, a.Color, a.ownerId,
                owners.TryGetValue(a.ownerId, out var ownerName) ? ownerName : null,
                bank?.ShortName, bank?.BrandColor);
        }).ToList();

        var alerts = alertsRaw.Select(a => new BudgetAlertRow(
            a.Id, a.AccountId, a.RuleId, a.Kind, a.Level, a.Message, a.BalanceSnapshot,
            a.CreatedAt,
            a.AccountId is { } aid && accountNames.TryGetValue(aid, out var name) ? name : null)).ToList();

        var reserveLoans = reserveLoansRaw.Select(l => new BudgetReserveLoanRow(
            l.Id, l.AccountId, l.RuleId, l.Amount, l.TakenAt, l.DueDate, l.Comment,
            accountNames.TryGetValue(l.AccountId, out var loanAccountName) ? loanAccountName : null,
            l.DueDate < DateTime.UtcNow.Date)).ToList();

        var reserveRules = await _db.AccountRules
            .Where(r => r.UserId == uid && r.Kind == AccountRuleKind.EmergencyReserve
                        && r.IsEnabled && r.Threshold != null)
            .OrderBy(r => r.AccountId == null ? 0 : 1).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var totalNonCredit = accounts.Where(a => a.Type != AccountType.CreditCard).Sum(a => a.Balance);
        var reserveTaken = reserveLoans.Sum(l => l.Amount);
        var reserves = reserveRules.Select(r =>
        {
            var balance = r.AccountId is { } aid
                ? accounts.FirstOrDefault(a => a.Id == aid)?.Balance ?? 0
                : totalNonCredit;
            var taken = r.AccountId is { } aid2
                ? reserveLoans.Where(l => l.AccountId == aid2).Sum(l => l.Amount)
                : reserveTaken;
            var threshold = r.Threshold ?? 0;
            return new BudgetReserveRow(
                r.Id, r.Name, r.AccountId,
                r.AccountId is { } ra ? accounts.FirstOrDefault(a => a.Id == ra)?.Name : null,
                threshold, balance, taken,
                AccountRulesService.FreeReserve(balance, threshold, taken));
        }).ToList();

        // Итог по счетам: активные счета минус долги по кредиткам.
        // Если счетов нет — считаем по старому механизму (стартовый баланс + все операции).
        // Переводы между своими счетами не доход и не расход: они влияют только на балансы.
        // При охвате family/ids (N4) считаем по счетам всех выбранных пользователей;
        // у кого счетов нет — берём стартовый баланс + все его операции.
        var analytics = txs.Where(t => !t.IsTransfer).ToList();
        var scopeAccounts = await _db.Accounts
            .Where(a => scopeUserIds.Contains(a.UserId) && !a.IsArchived).ToListAsync(ct);
        var noAccountUserIds = scopeUserIds
            .Where(id => !scopeAccounts.Any(a => a.UserId == id)).ToList();
        decimal totalBalance = scopeAccounts.Sum(a => a.Type == AccountType.CreditCard ? -a.Balance : a.Balance);
        // B15: один запрос на все стартовые балансы вместо запроса на каждого пользователя.
        // Себя берём из уже загруженной записи user — как и раньше.
        var missingUserIds = noAccountUserIds.Where(id => id != uid).ToList();
        var startingBalances = missingUserIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.Users.AsNoTracking().Where(u => missingUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.StartingBalance })
                .ToDictionaryAsync(u => u.Id, u => u.StartingBalance, ct);
        foreach (var id in noAccountUserIds)
        {
            var start = id == uid ? user?.StartingBalance ?? 0
                : startingBalances.GetValueOrDefault(id);
            totalBalance += start + analytics
                .Where(t => t.UserId == id)
                .Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
        }
        var cashbackTotal = scopeAccounts.Sum(a => a.CashbackAccrued);

        var nowMonth = analytics.Where(t => t.Date.Year == year && t.Date.Month == month).ToList();
        var income = nowMonth.Where(t => t.Type == TxType.Income).Sum(t => t.Amount);
        var expense = nowMonth.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount);

        var byMonth = analytics.GroupBy(t => new { t.Date.Year, t.Date.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new BudgetMonthRow(
                g.Key.Year, g.Key.Month,
                g.Where(t => t.Type == TxType.Income).Sum(t => t.Amount),
                g.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount))).ToList();

        // Структура расходов за выбранный месяц (по категориям).
        // После 0.3.7 категории общие для семьи: категория может принадлежать другому
        // члену семьи, поэтому ищем только по Id (UserId совпадать не обязан).
        // Группируем по названию без учёта регистра для совместимости со старыми данными.
        Category? CatOf(Transaction t) => cats.FirstOrDefault(c => c.Id == t.CategoryId);
        var expenseShare = nowMonth.Where(t => t.Type == TxType.Expense)
            .GroupBy(t => CatOf(t)?.Name ?? "—", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cat = CatOf(g.First());
                return new BudgetExpenseShareRow(
                    g.First().CategoryId,
                    cat?.Name ?? g.Key,
                    cat?.Color ?? "#718096",
                    g.Sum(t => t.Amount));
            })
            .OrderByDescending(x => x.Total)
            .ToList();

        // Лимиты выбранного месяца + факт расходов
        var limitRows = limits.Select(l => new BudgetLimitRow(
            l.Id, l.CategoryId, l.Limit,
            cats.FirstOrDefault(c => c.Id == l.CategoryId)?.Name ?? "—",
            cats.FirstOrDefault(c => c.Id == l.CategoryId)?.Color ?? "#718096",
            nowMonth.Where(t => t.CategoryId == l.CategoryId && t.Type == TxType.Expense)
                .Sum(t => t.Amount))).ToList();

        var goals = await _db.SavingsGoals.Where(g => g.UserId == uid).OrderBy(g => g.Deadline).ToListAsync(ct);
        var goalRows = goals.Select(g =>
        {
            decimal? perMonth = null; int? monthsLeft = null;
            if (g.Deadline is { } dl)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var remaining = Math.Max(0, g.TargetAmount - g.CurrentAmount);
                monthsLeft = Math.Max(0, (dl.Year - today.Year) * 12 + (dl.Month - today.Month) + (dl.Day >= today.Day ? 0 : -1) + 1);
                perMonth = monthsLeft > 0 ? Math.Round(remaining / monthsLeft.Value, 0) : remaining;
            }
            return new BudgetGoalRow(
                g.Id, g.Name, g.TargetAmount, g.CurrentAmount, g.Deadline,
                g.TargetAmount > 0 ? Math.Round(g.CurrentAmount / g.TargetAmount * 100, 1) : 0,
                monthsLeft, perMonth);
        }).ToList();

        // ---------- Персонализированные рекомендации ----------
        // При охвате family/ids показываем только общие советы: лимиты, цели и подушка — личные.
        var recommendations = BuildRecommendations(year, month, txs, cats, limits, nowMonth, income, expense, goals,
            scopeUserIds.Count == 1);

        // Рекомендация по кэшбэку (если есть счета со ставкой)
        if (cashbackTotal > 0)
            recommendations.Insert(0, new BudgetRecommendationRow("good", "💳",
                $"По вашим счетам накоплен кэшбэк {cashbackTotal:0} ₽ — проверьте, начислил ли банк его на баланс."));

        // Список членов семьи для переключателя (только когда семья действительно есть).
        var scopeUsersRaw = familyIds.Count > 1
            ? await _db.Users.Where(u => familyIds.Contains(u.Id)).OrderBy(u => u.Id)
                .Select(u => new { u.Id, u.Name }).ToListAsync(ct)
            : null;
        var scopeUsers = scopeUsersRaw?.Select(u => new BudgetScopeUserRow(u.Id, u.Name, u.Id == uid)).ToList();

        // accounts: банковские реквизиты из одного пакетного справочника (B15).
        var accountRows = accounts.Select(a =>
        {
            var bank = BankOf(a.BankId);
            return new BudgetAccountRow(
                a.Id, a.Name, a.Type, AccountTypeNames.Ru(a.Type), a.Balance, a.CreditLimit,
                a.CashbackRate, a.CashbackAccrued, a.Color, bank?.ShortName, bank?.BrandColor);
        }).ToList();

        return new BudgetSummaryResult(
            year, month,
            // Охват агрегатов (N4): me | family | ids + список членов семьи для переключателя
            effectiveScope,
            scopeUserIds,
            // N4, совместимость с обоими вариантами API (я/семья/выбранные).
            effectiveScope == "me" ? "Только я"
                : effectiveScope == "family" ? "Вся семья" : "Выбранные",
            scopeUserIds,
            scopeUsers,
            user?.StartingBalance ?? 0,
            income - expense,
            income,
            expense,
            totalBalance,
            income > 0 ? Math.Round((income - expense) / income * 100, 1) : 0,
            byMonth,
            expenseShare,
            limitRows,
            goalRows,
            accountRows,
            familyAccounts,
            familyAccounts.Sum(a => a.Balance),
            cashbackTotal,
            recommendations,
            alerts,
            reserveLoans,
            reserves,
            reserveTaken);
    }

    private static List<BudgetRecommendationRow> BuildRecommendations(
        int year, int month, List<Transaction> txs, List<Category> cats, List<BudgetLimit> limits,
        List<Transaction> nowMonth, decimal income, decimal expense, List<SavingsGoal> goals,
        bool personal = true)
    {
        var recs = new List<BudgetRecommendationRow>();

        void Add(string level, string icon, string text) => recs.Add(new BudgetRecommendationRow(level, icon, text));

        // 1. Расход больше дохода в этом месяце
        if (expense > income && expense > 0)
            Add("danger", "⚠️",
                $"В этом месяце расходы превышают доходы на {expense - income:0} ₽. Отложите необязательные траты и разберите категории «Развлечения» и «Одежда».");

        // 2. Превышение лимитов категорий (лимиты личные — только при охвате «только я»)
        if (personal)
            foreach (var l in limits)
            {
                var spent = nowMonth.Where(t => t.CategoryId == l.CategoryId && t.Type == TxType.Expense).Sum(t => t.Amount);
                if (spent > l.Limit)
                {
                    var cname = cats.FirstOrDefault(c => c.Id == l.CategoryId)?.Name ?? "категория";
                    Add("danger", "📊",
                        $"Лимит по категории «{cname}» превышен на {spent - l.Limit:0} ₽. Поднимите лимит или урежьте траты до конца месяца.");
                }
            }

        // 3. Нет лимита на крупную категорию расходов (личная рекомендация)
        if (personal)
        {
            var bigNoLimit = nowMonth.Where(t => t.Type == TxType.Expense)
                .GroupBy(t => t.CategoryId)
                .Where(g => !limits.Any(l => l.CategoryId == g.Key))
                .OrderByDescending(g => g.Sum(t => t.Amount))
                .FirstOrDefault();
            if (bigNoLimit is not null && income > 0)
            {
                var cname = cats.FirstOrDefault(c => c.Id == bigNoLimit.Key)?.Name ?? "категория";
                var share = bigNoLimit.Sum(t => t.Amount) / income * 100;
                if (share > 25)
                    Add("warn", "💡",
                        $"На «{cname}» уходит {share:0}% дохода, а лимита нет. Задайте лимит — это самая частая «дыра» в бюджете.");
            }
        }

        // 4. Норма сбережений
        if (income > 0)
        {
            var rate = (income - expense) / income * 100;
            if (rate < 10)
                Add("warn", "🎯",
                    $"Вы откладываете всего {rate:0}% дохода. Финансовая подушка безопасности — 3–6 месячных расходов; начните с 10%.");
            else if (rate >= 20)
                Add("good", "👍",
                    $"Отлично: вы сохраняете {rate:0}% дохода. Так держать — направьте излишек в цели с сроком.");
        }

        // 5. Подушка безопасности (личная рекомендация)
        if (personal)
        {
            var avgExpense = txs.Where(t => !t.IsTransfer && t.Type == TxType.Expense)
                .GroupBy(t => new { t.Date.Year, t.Date.Month })
                .Select(g => g.Sum(t => t.Amount)).DefaultIfEmpty(0).Average();
            var cushionMonths = avgExpense > 0 ? Math.Round(((decimal?)goals.Sum(g => g.CurrentAmount) ?? 0) / avgExpense, 1) : 0;
            if (avgExpense > 0 && cushionMonths < 3)
                Add("warn", "🛟",
                    $"Ваших копилок хватает на {cushionMonths} мес. расходов. Целься в подушку на 3–6 месяцев — создайте цель «Резерв».");

            // 6. Цели без срока (личная рекомендация)
            var noDeadline = goals.Where(g => g.Deadline is null).ToList();
            if (noDeadline.Count > 0)
                Add("info", "📅",
                    $"У {noDeadline.Count} целей нет срока. Добавьте дату — так проще рассчитать, сколько откладывать в месяц.");
        }

        // 7. Регулярность ведения учёта
        var lastTx = txs.OrderByDescending(t => t.Date).FirstOrDefault();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (lastTx is null)
            Add("info", "✍️", "Запишите первую операцию — учёт начинает работать, когда данные попадают внутрь регулярно.");
        else if ((today.DayNumber - lastTx.Date.DayNumber) > 7)
            Add("info", "✍️", "Вы не заносили операции больше недели. 2 минуты в день — и бюджет всегда актуален.");

        if (recs.Count == 0)
            Add("good", "✅", "С бюджетом всё в порядке: лимиты соблюдаются, нормы сбережений в плюсе.");

        return recs;
    }
}
