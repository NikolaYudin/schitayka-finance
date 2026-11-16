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

    // Сводка бюджета + персональные рекомендации (N4: охват — я / семья / выбранные члены).
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int year = 0, [FromQuery] int month = 0,
        [FromQuery] string scope = "me", [FromQuery] string? ids = null)
    {
        var uid = CurrentUserId;
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (year <= 0 || month <= 0) { year = DateTime.Today.Year; month = DateTime.Today.Month; }

        var familyAll = await SchitayKa.Api.Services.FamilyAccess.ManageableUserIdsAsync(_db, uid);
        var scopeNorm = (scope ?? "me").Trim().ToLowerInvariant();
        List<int> targetIds = [uid];
        if (scopeNorm == "family")
        {
            targetIds = familyAll.Count > 0 ? familyAll : [uid];
        }
        else if (scopeNorm == "ids" && !string.IsNullOrWhiteSpace(ids))
        {
            var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var x) ? x : (int?)null)
                .Where(x => x.HasValue).Select(x => x!.Value)
                .Distinct().Where(familyAll.Contains).ToList();
            if (parsed.Count > 0) targetIds = parsed;
        }
        else
        {
            scopeNorm = "me";
            targetIds = [uid];
        }

        foreach (var tid in targetIds) await _rules.EvaluateAllAsync(tid);

        var users = await _db.Users.Where(u => targetIds.Contains(u.Id)).ToListAsync();
        var txs = await _db.Transactions.Where(t => targetIds.Contains(t.UserId)).ToListAsync();
        var cats = await _db.Categories.Where(c => targetIds.Contains(c.UserId)).ToListAsync();
        var limits = await _db.BudgetLimits.Where(l => targetIds.Contains(l.UserId) && l.Year == year && l.Month == month).ToListAsync();
        var accounts = await _db.Accounts.Where(a => targetIds.Contains(a.UserId) && !a.IsArchived).ToListAsync();

        // При личном охвате отдельно отдаём счета семьи (как раньше).
        var familyAccounts = new List<object>();
        decimal familyTotalBalance = 0m;
        if (scopeNorm == "me")
        {
            var familyAccRows = await _db.Accounts
                .Where(a => familyAll.Contains(a.UserId) && a.UserId != uid && !a.IsArchived)
                .OrderBy(a => a.UserId).ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
                    a.Balance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color,
                    ownerId = a.UserId,
                    ownerName = _db.Users.Where(u => u.Id == a.UserId).Select(u => u.Name).FirstOrDefault(),
                })
                .ToListAsync();
            familyTotalBalance = familyAccRows.Sum(a => a.Balance);
            familyAccounts.AddRange(familyAccRows);
        }

        var alerts = await _db.AccountAlerts
            .Where(a => targetIds.Contains(a.UserId) && a.ResolvedAt == null && a.DismissedAt == null)
            .OrderBy(a => a.Level == "danger" ? 0 : 1).ThenByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.UserId, a.AccountId, a.RuleId, a.Kind, a.Level, a.Message, a.BalanceSnapshot, a.CreatedAt,
                accountName = a.AccountId == null ? null
                    : _db.Accounts.Where(x => x.Id == a.AccountId).Select(x => x.Name).FirstOrDefault(),
            })
            .ToListAsync();

        var reserveLoans = await _db.ReserveLoans
            .Where(l => targetIds.Contains(l.UserId) && l.ReturnedAt == null)
            .OrderBy(l => l.DueDate).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.UserId, l.AccountId, l.RuleId, l.Amount, l.TakenAt, l.DueDate, l.Comment,
                accountName = _db.Accounts.Where(a => a.Id == l.AccountId).Select(a => a.Name).FirstOrDefault(),
                isOverdue = l.DueDate < DateTime.UtcNow.Date,
            })
            .ToListAsync();

        var reserveRules = await _db.AccountRules
            .Where(r => targetIds.Contains(r.UserId) && r.Kind == AccountRuleKind.EmergencyReserve
                        && r.IsEnabled && r.Threshold != null)
            .OrderBy(r => r.AccountId == null ? 0 : 1).ThenBy(r => r.Id)
            .ToListAsync();

        var reserveTaken = reserveLoans.Sum(l => l.Amount);
        var reserves = reserveRules.Select(r =>
        {
            var ownerAccounts = accounts.Where(a => a.UserId == r.UserId).ToList();
            var totalNonCredit = ownerAccounts.Where(a => a.Type != AccountType.CreditCard).Sum(a => a.Balance);
            var balance = r.AccountId is { } aid
                ? ownerAccounts.FirstOrDefault(a => a.Id == aid)?.Balance ?? 0
                : totalNonCredit;
            var ownerTaken = reserveLoans.Where(l => l.UserId == r.UserId).Sum(l => l.Amount);
            var taken = r.AccountId is { } aid2
                ? reserveLoans.Where(l => l.UserId == r.UserId && l.AccountId == aid2).Sum(l => l.Amount)
                : ownerTaken;
            var threshold = r.Threshold ?? 0;
            return new
            {
                ruleId = r.Id, ruleName = r.Name, r.AccountId,
                accountName = r.AccountId is { } ra
                    ? ownerAccounts.FirstOrDefault(a => a.Id == ra)?.Name
                    : null,
                threshold, balance, taken,
                free = SchitayKa.Api.Services.AccountRulesService.FreeReserve(balance, threshold, taken),
            };
        }).ToList();

        // Переводы между счетами не доход и не расход: влияют только на балансы.
        var analytics = txs.Where(t => !t.IsTransfer).ToList();
        var catById = cats.ToDictionary(c => c.Id);
        decimal totalBalance = accounts.Sum(a => a.Type == AccountType.CreditCard ? -a.Balance : a.Balance);
        if (accounts.Count == 0)
        {
            var starting = users.Sum(u => u.StartingBalance);
            totalBalance = starting + analytics.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
        }
        var cashbackTotal = accounts.Sum(a => a.CashbackAccrued);

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

        // Структура расходов за месяц (в семейном охвате объединяем по названию категории).
        var expenseShare = nowMonth.Where(t => t.Type == TxType.Expense)
            .GroupBy(t =>
            {
                var c = catById.GetValueOrDefault(t.CategoryId);
                return $"{c?.Name ?? "—"}|{c?.Color ?? "#718096"}";
            })
            .Select(g =>
            {
                var c = catById.GetValueOrDefault(g.First().CategoryId);
                return new
                {
                    categoryId = g.First().CategoryId,
                    name = c?.Name ?? "—",
                    color = c?.Color ?? "#718096",
                    total = g.Sum(t => t.Amount),
                };
            })
            .OrderByDescending(x => x.total)
            .ToList();

        // Лимиты выбранного месяца + факт расходов (по владельцу категории).
        var limitRows = limits.Select(l =>
        {
            var c = cats.FirstOrDefault(x => x.Id == l.CategoryId);
            return new
            {
                l.Id, l.CategoryId, l.UserId, l.Limit,
                name = c?.Name ?? "—",
                color = c?.Color ?? "#718096",
                spent = nowMonth.Where(t => t.UserId == l.UserId && t.CategoryId == l.CategoryId && t.Type == TxType.Expense).Sum(t => t.Amount),
            };
        }).ToList();

        var goals = await _db.SavingsGoals.Where(g => targetIds.Contains(g.UserId)).OrderBy(g => g.Deadline).ToListAsync();
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
                g.Id, g.UserId, g.Name, g.TargetAmount, g.CurrentAmount, g.Deadline,
                progress = g.TargetAmount > 0 ? Math.Round(g.CurrentAmount / g.TargetAmount * 100, 1) : 0,
                monthsLeft, perMonth,
            };
        }).ToList();

        // ---------- Персонализированные рекомендации ----------
        var recommendations = BuildRecommendations(year, month, txs, cats, limits, nowMonth, income, expense, goals);
        if (cashbackTotal > 0)
            recommendations.Insert(0, new { level = "good", icon = "💳",
                text = $"Накоплен кэшбэк {cashbackTotal:0} ₽ — проверьте, начислил ли банк его на баланс." });

        var scopeLabel = scopeNorm == "family" ? "Вся семья"
            : scopeNorm == "ids" ? $"Выбрано: {targetIds.Count}"
            : "Только я";

        var accountRows = accounts
            .OrderBy(a => a.UserId != uid).ThenBy(a => a.UserId).ThenBy(a => a.Type).ThenBy(a => a.Name)
            .Select(a => new
            {
                a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
                a.Balance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color,
                ownerId = a.UserId,
                ownerName = users.FirstOrDefault(u => u.Id == a.UserId)?.Name ?? "",
                isFamily = a.UserId != uid,
            }).ToList();

        return Ok(new
        {
            year, month,
            scope = scopeNorm,
            scopeLabel,
            scopeUserIds = targetIds,
            startingBalance = users.Sum(u => u.StartingBalance),
            balance = income - expense,
            monthIncome = income,
            monthExpense = expense,
            totalBalance,
            savingsRate = income > 0 ? Math.Round((income - expense) / income * 100, 1) : 0,
            byMonth,
            expenseShare,
            limits = limitRows,
            goals = goalRows,
            accounts = accountRows,
            familyAccounts,
            familyTotalBalance,
            cashbackTotal,
            recommendations,
            alerts,
            reserveLoans,
            reserves,
            reserveTaken,
        });
    }

    private static List<dynamic> BuildRecommendations(
        int year, int month, List<Transaction> txs, List<Category> cats, List<BudgetLimit> limits,
        List<Transaction> nowMonth, decimal income, decimal expense, List<SavingsGoal> goals)
    {
        var recs = new List<dynamic>();

        void Add(string level, string icon, string text) => recs.Add(new { level, icon, text });

        // 1. Расход больше дохода в этом месяце
        if (expense > income && expense > 0)
            Add("danger", "⚠️",
                $"В этом месяце расходы превышают доходы на {expense - income:0} ₽. Отложите необязательные траты и разберите категории «Развлечения» и «Одежда».");

        // 2. Превышение лимитов категорий
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

        // 3. Нет лимита на крупную категорию расходов
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

        // 5. Подушка безопасности
        var avgExpense = txs.Where(t => !t.IsTransfer && t.Type == TxType.Expense)
            .GroupBy(t => new { t.Date.Year, t.Date.Month })
            .Select(g => g.Sum(t => t.Amount)).DefaultIfEmpty(0).Average();
        var cushionMonths = avgExpense > 0 ? Math.Round(((decimal?)goals.Sum(g => g.CurrentAmount) ?? 0) / avgExpense, 1) : 0;
        if (avgExpense > 0 && cushionMonths < 3)
            Add("warn", "🛟",
                $"Ваших копилок хватает на {cushionMonths} мес. расходов. Целься в подушку на 3–6 месяцев — создайте цель «Резерв».");

        // 6. Цели без срока
        var noDeadline = goals.Where(g => g.Deadline is null).ToList();
        if (noDeadline.Count > 0)
            Add("info", "📅",
                $"У {noDeadline.Count} целей нет срока. Добавьте дату — так проще рассчитать, сколько откладывать в месяц.");

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
