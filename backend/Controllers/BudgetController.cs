using System.Security.Claims;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Authorize] // без валидного JWT запрос не дойдёт до контроллера — вернёт 401, а не 500
[Route("api/budget")]
public class BudgetController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SchitayKa.Api.Services.AccountRulesService _rules;
    public BudgetController(AppDbContext db, SchitayKa.Api.Services.AccountRulesService rules)
    { _db = db; _rules = rules; }

    private int CurrentUserId => UserAccess.UidOf(User);

    public record StartBalanceDto(decimal StartingBalance);
    public record TxCreateDto(int CategoryId, TxType Type, decimal Amount, DateOnly? Date, string? Comment, int? AccountId, int? ForUserId);

    // Создать операцию для члена семьи (доступно только владельцу семейной группы)
    [HttpPost("tx-for")]
    public async Task<IActionResult> CreateTxForFamily(TxCreateDto dto)
    {
        var uid = CurrentUserId;
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма должна быть больше нуля" });
        var me = await _db.Users.FindAsync(uid);
        if (me is null || string.IsNullOrEmpty(me.FamilyCode))
            return BadRequest(new { error = "Вы не в семье" });
        // Владелец группы: тот, кто создавал приглашения (есть FamilyInvitations с OwnerUserId) либо первый участник
        var isOwner = await _db.FamilyInvitations.AnyAsync(i => i.OwnerUserId == uid && i.AcceptedAt != null);
        if (!isOwner)
        {
            var firstMemberId = await _db.Users.Where(u => u.FamilyCode == me.FamilyCode).OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
            isOwner = firstMemberId == uid;
        }
        if (!isOwner) return Forbid();
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == dto.ForUserId && u.FamilyCode == me.FamilyCode);
        if (target is null) return BadRequest(new { error = "Пользователь не найден в вашей семье" });

        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.UserId == target.Id);
        if (cat is null) return BadRequest(new { error = "Категория не найдена у этого пользователя" });
        if (cat.Type != dto.Type) return BadRequest(new { error = "Тип операции не совпадает с категорией" });

        int? accId = dto.AccountId ?? (await _db.Accounts.FirstOrDefaultAsync(a => a.UserId == target.Id && !a.IsArchived))?.Id;
        if (accId.HasValue)
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accId.Value && a.UserId == target.Id && !a.IsArchived);
            if (acc is null) return BadRequest(new { error = "Счёт не найден у этого пользователя" });
        }
        var tx = new Transaction
        {
            UserId = target.Id,
            CategoryId = dto.CategoryId,
            Type = dto.Type,
            Amount = dto.Amount,
            Date = dto.Date ?? DateOnly.FromDateTime(DateTime.Today),
            Comment = dto.Comment?.Trim() ?? "",
            AccountId = accId,
        };
        _db.Transactions.Add(tx);

        // Операция и пересчёт баланса счёта получателя должны применяться вместе:
        // иначе при ошибке клиент получит 500, а операция останется в базе.
        await using var tr = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        if (tx.AccountId is { } txAccId)
        {
            await AccountsController.RecalcAccountsOf(_db, _db.Accounts.Where(a => a.Id == txAccId));
            await _rules.EvaluateAllAsync(target.Id);
        }
        await tr.CommitAsync();

        return Ok(new { tx.Id });
    }

    // Установить стартовый баланс («сколько денег на счетах сейчас»)
    [HttpPost("start-balance")]
    public async Task<IActionResult> SetStartBalance(StartBalanceDto dto)
    {
        var uid = CurrentUserId;
        var user = await _db.Users.FindAsync(uid);
        if (user is null) return Unauthorized();
        user.StartingBalance = dto.StartingBalance;
        await _db.SaveChangesAsync();
        return Ok(new { user.StartingBalance });
    }

    // Сводка личного бюджета + персональные рекомендации.
    // 0.3/N4: параметр охвата — me (по умолчанию, свои данные), family (все члены семьи)
    // или ids (выбранные члены через параметр ids=1,2,3; проверяются по FamilyAccess).
    // Личные инструменты (лимиты, цели, уведомления, резервы) всегда считаются по текущему
    // пользователю — у семьи они свои у каждого.
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int year = 0, [FromQuery] int month = 0,
        [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        var uid = CurrentUserId;
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (year <= 0 || month <= 0) { year = DateTime.Today.Year; month = DateTime.Today.Month; }

        // Обновляем уведомления по правилам счетов перед отдачей сводки:
        // срок займа мог наступить без новых операций.
        await _rules.EvaluateAllAsync(uid);

        var user = await _db.Users.FindAsync(uid);

        // ---------- Охват агрегатов (N4) ----------
        var familyIds = await SchitayKa.Api.Services.FamilyAccess.ManageableUserIdsAsync(_db, uid);
        var (effectiveScope, scopeUserIds) = ResolveScope(scope, ids, uid, familyIds);

        var txs = await _db.Transactions.Where(t => scopeUserIds.Contains(t.UserId)).ToListAsync();
        var cats = await _db.Categories.Where(c => scopeUserIds.Contains(c.UserId)).ToListAsync();
        var limits = await _db.BudgetLimits.Where(l => l.UserId == uid && l.Year == year && l.Month == month).ToListAsync();
        var accounts = await _db.Accounts.Where(a => a.UserId == uid && !a.IsArchived).ToListAsync();

        // Счета семьи: видны всем членам группы, но не влияют на личный бюджет
        // (доходы/расходы считаются только по собственным операциям).
        var familyAccounts = await _db.Accounts
            .Where(a => familyIds.Contains(a.UserId) && a.UserId != uid && !a.IsArchived)
            .OrderBy(a => a.UserId).ThenBy(a => a.Name)
            .Select(a => new
            {
                a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
                a.Balance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color,
                ownerId = a.UserId,
                ownerName = _db.Users.Where(u => u.Id == a.UserId).Select(u => u.Name).FirstOrDefault(),
                bankShortName = _db.Banks.Where(b => b.Id == a.BankId).Select(b => b.ShortName).FirstOrDefault(),
                bankColor = _db.Banks.Where(b => b.Id == a.BankId).Select(b => b.BrandColor).FirstOrDefault(),
            })
            .ToListAsync();

        var alerts = await _db.AccountAlerts
            .Where(a => a.UserId == uid && a.ResolvedAt == null && a.DismissedAt == null)
            .OrderBy(a => a.Level == "danger" ? 0 : 1).ThenByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.AccountId, a.RuleId, a.Kind, a.Level, a.Message, a.BalanceSnapshot, a.CreatedAt,
                accountName = a.AccountId == null ? null
                    : _db.Accounts.Where(x => x.Id == a.AccountId).Select(x => x.Name).FirstOrDefault(),
            })
            .ToListAsync();

        var reserveLoans = await _db.ReserveLoans
            .Where(l => l.UserId == uid && l.ReturnedAt == null)
            .OrderBy(l => l.DueDate).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.AccountId, l.RuleId, l.Amount, l.TakenAt, l.DueDate, l.Comment,
                accountName = _db.Accounts.Where(a => a.Id == l.AccountId).Select(a => a.Name).FirstOrDefault(),
                isOverdue = l.DueDate < DateTime.UtcNow.Date,
            })
            .ToListAsync();

        var reserveRules = await _db.AccountRules
            .Where(r => r.UserId == uid && r.Kind == AccountRuleKind.EmergencyReserve
                        && r.IsEnabled && r.Threshold != null)
            .OrderBy(r => r.AccountId == null ? 0 : 1).ThenBy(r => r.Id)
            .ToListAsync();

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
            return new
            {
                ruleId = r.Id, ruleName = r.Name, r.AccountId,
                accountName = r.AccountId is { } ra
                    ? accounts.FirstOrDefault(a => a.Id == ra)?.Name
                    : null,
                threshold, balance, taken,
                free = SchitayKa.Api.Services.AccountRulesService.FreeReserve(balance, threshold, taken),
            };
        }).ToList();

        // Итог по счетам: активные счета минус долги по кредиткам.
        // Если счетов нет — считаем по старому механизму (стартовый баланс + все операции).
        // Переводы между своими счетами не доход и не расход: они влияют только на балансы.
        // При охвате family/ids (N4) считаем по счетам всех выбранных пользователей;
        // у кого счетов нет — берём стартовый баланс + все его операции.
        var analytics = txs.Where(t => !t.IsTransfer).ToList();
        var scopeAccounts = await _db.Accounts
            .Where(a => scopeUserIds.Contains(a.UserId) && !a.IsArchived).ToListAsync();
        var noAccountUserIds = scopeUserIds
            .Where(id => !scopeAccounts.Any(a => a.UserId == id)).ToList();
        decimal totalBalance = scopeAccounts.Sum(a => a.Type == AccountType.CreditCard ? -a.Balance : a.Balance);
        foreach (var id in noAccountUserIds)
        {
            var start = id == uid ? user?.StartingBalance ?? 0
                : await _db.Users.Where(u => u.Id == id).Select(u => (decimal?)u.StartingBalance).FirstOrDefaultAsync() ?? 0;
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
            .Select(g => new
            {
                year = g.Key.Year, month = g.Key.Month,
                income = g.Where(t => t.Type == TxType.Income).Sum(t => t.Amount),
                expense = g.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount),
            }).ToList();

        // Структура расходов за выбранный месяц (по категориям).
        // Группируем по НАЗВАНИЮ (без учёта регистра): идентификаторы категорий у каждого
        // пользователя свои, и один и тот же id — это разные категории, поэтому при охвате
        // family/ids одноимённые категории («Продукты» у мужа и жены) объединяются.
        Category? CatOf(Transaction t) => cats.FirstOrDefault(c => c.UserId == t.UserId && c.Id == t.CategoryId);
        var expenseShare = nowMonth.Where(t => t.Type == TxType.Expense)
            .GroupBy(t => CatOf(t)?.Name ?? "—", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cat = CatOf(g.First());
                return new
                {
                    categoryId = g.First().CategoryId,
                    name = cat?.Name ?? g.Key,
                    color = cat?.Color ?? "#718096",
                    total = g.Sum(t => t.Amount),
                };
            })
            .OrderByDescending(x => x.total)
            .ToList();

        // Лимиты выбранного месяца + факт расходов
        var limitRows = limits.Select(l => new
        {
            l.Id, l.CategoryId, l.Limit,
            name = cats.FirstOrDefault(c => c.Id == l.CategoryId)?.Name ?? "—",
            color = cats.FirstOrDefault(c => c.Id == l.CategoryId)?.Color ?? "#718096",
            spent = nowMonth.Where(t => t.CategoryId == l.CategoryId && t.Type == TxType.Expense).Sum(t => t.Amount),
        }).ToList();

        var goals = await _db.SavingsGoals.Where(g => g.UserId == uid).OrderBy(g => g.Deadline).ToListAsync();
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
            return (object)new
            {
                g.Id, g.Name, g.TargetAmount, g.CurrentAmount, g.Deadline,
                progress = g.TargetAmount > 0 ? Math.Round(g.CurrentAmount / g.TargetAmount * 100, 1) : 0,
                monthsLeft, perMonth,
            };
        }).ToList();

        // ---------- Персонализированные рекомендации ----------
        // При охвате family/ids показываем только общие советы: лимиты, цели и подушка — личные.
        var recommendations = BuildRecommendations(year, month, txs, cats, limits, nowMonth, income, expense, goals,
            scopeUserIds.Count == 1);

        // Рекомендация по кэшбэку (если есть счета со ставкой)
        if (cashbackTotal > 0)
            recommendations.Insert(0, new { level = "good", icon = "💳",
                text = $"По вашим счетам накоплен кэшбэк {cashbackTotal:0} ₽ — проверьте, начислил ли банк его на баланс." });

        return Ok(new
        {
            year, month,
            // Охват агрегатов (N4): me | family | ids + список членов семьи для переключателя
            scope = effectiveScope,
            scopeIds = scopeUserIds,
            // N4, совместимость с обоими вариантами API (я/семья/выбранные).
            scopeLabel = effectiveScope == "me" ? "Только я"
                : effectiveScope == "family" ? "Вся семья" : "Выбранные",
            scopeUserIds = scopeUserIds,
            scopeUsers = familyIds.Count > 1
                ? await _db.Users
                    .Where(u => familyIds.Contains(u.Id))
                    .OrderBy(u => u.Id)
                    .Select(u => new { id = u.Id, name = u.Name, isMe = u.Id == uid })
                    .ToListAsync()
                : null,
            startingBalance = user?.StartingBalance ?? 0,
            balance = income - expense,
            monthIncome = income,
            monthExpense = expense,
            totalBalance,
            savingsRate = income > 0 ? Math.Round((income - expense) / income * 100, 1) : 0,
            byMonth,
            expenseShare,
            limits = limitRows,
            goals = goalRows,
            accounts = accounts.Select(a => new
            {
                a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
                a.Balance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color,
                bankShortName = a.BankId is int bid
                    ? _db.Banks.Where(b => b.Id == bid).Select(b => b.ShortName).FirstOrDefault()
                    : null,
                bankColor = a.BankId is int bid2
                    ? _db.Banks.Where(b => b.Id == bid2).Select(b => b.BrandColor).FirstOrDefault()
                    : null,
            }),
            familyAccounts,
            familyTotalBalance = familyAccounts.Sum(a => a.Balance),
            cashbackTotal,
            recommendations,
            alerts,
            reserveLoans,
            reserves,
            reserveTaken,
        });
    }

    /// <summary>
    /// Разбор параметра охвата (0.3/N4). Возвращает эффективный охват и идентификаторы
    /// пользователей, по которым агрегировать. Чужие id отбрасываются — агрегировать можно
    /// только себя и членов своей семьи (правило единственное место — FamilyAccess).
    /// </summary>
    private static (string Scope, List<int> UserIds) ResolveScope(string? scope, string? ids, int uid, List<int> familyIds)
    {
        switch ((scope ?? "me").Trim().ToLowerInvariant())
        {
            case "family" when familyIds.Count > 1:
                return ("family", familyIds);
            case "ids":
            {
                var parsed = (ids ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : -1)
                    .Where(n => n > 0 && familyIds.Contains(n))
                    .Distinct()
                    .ToList();
                if (parsed.Count > 1 || (parsed.Count == 1 && parsed[0] != uid))
                    return ("ids", parsed);
                return ("me", [uid]); // нет валидных выбранных — работаем как «только я»
            }
            default:
                return ("me", [uid]);
        }
    }

    private static List<dynamic> BuildRecommendations(
        int year, int month, List<Transaction> txs, List<Category> cats, List<BudgetLimit> limits,
        List<Transaction> nowMonth, decimal income, decimal expense, List<SavingsGoal> goals,
        bool personal = true)
    {
        var recs = new List<dynamic>();

        void Add(string level, string icon, string text) => recs.Add(new { level, icon, text });

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
