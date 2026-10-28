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
    public BudgetController(AppDbContext db) { _db = db; }

    private int CurrentUserId => UserAccess.UidOf(User);

    public record StartBalanceDto(decimal StartingBalance);

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

    // Сводка личного бюджета + персональные рекомендации
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int year = 0, [FromQuery] int month = 0)
    {
        var uid = CurrentUserId;
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (year <= 0 || month <= 0) { year = DateTime.Today.Year; month = DateTime.Today.Month; }

        var user = await _db.Users.FindAsync(uid);
        var txs = await _db.Transactions.Where(t => t.UserId == uid).ToListAsync();
        var cats = await _db.Categories.Where(c => c.UserId == uid).ToListAsync();
        var limits = await _db.BudgetLimits.Where(l => l.UserId == uid && l.Year == year && l.Month == month).ToListAsync();
        var accounts = await _db.Accounts.Where(a => a.UserId == uid && !a.IsArchived).ToListAsync();

        // Итог по счетам: активные счета минус долги по кредиткам.
        // Если счетов нет — считаем по старому механизму (стартовый баланс + все операции).
        decimal totalBalance = accounts.Count > 0
            ? accounts.Sum(a => a.Type == AccountType.CreditCard ? -a.Balance : a.Balance)
            : (user?.StartingBalance ?? 0) + txs.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
        var cashbackTotal = accounts.Sum(a => a.CashbackAccrued);

        var nowMonth = txs.Where(t => t.Date.Year == year && t.Date.Month == month).ToList();
        var income = nowMonth.Where(t => t.Type == TxType.Income).Sum(t => t.Amount);
        var expense = nowMonth.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount);

        var byMonth = txs.GroupBy(t => new { t.Date.Year, t.Date.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new
            {
                year = g.Key.Year, month = g.Key.Month,
                income = g.Where(t => t.Type == TxType.Income).Sum(t => t.Amount),
                expense = g.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount),
            }).ToList();

        // Структура расходов за выбранный месяц (по категориям)
        var expenseShare = nowMonth.Where(t => t.Type == TxType.Expense)
            .GroupBy(t => t.CategoryId)
            .Select(g => new
            {
                categoryId = g.Key,
                name = cats.FirstOrDefault(c => c.Id == g.Key)?.Name ?? "—",
                color = cats.FirstOrDefault(c => c.Id == g.Key)?.Color ?? "#718096",
                total = g.Sum(t => t.Amount),
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
        var recommendations = BuildRecommendations(year, month, txs, cats, limits, nowMonth, income, expense, goals);

        // Рекомендация по кэшбэку (если есть счета со ставкой)
        if (cashbackTotal > 0)
            recommendations.Insert(0, new { level = "good", icon = "💳",
                text = $"По вашим счетам накоплен кэшбэк {cashbackTotal:0} ₽ — проверьте, начислил ли банк его на баланс." });

        return Ok(new
        {
            year, month,
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
            }),
            cashbackTotal,
            recommendations,
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
        var avgExpense = txs.Where(t => t.Type == TxType.Expense)
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
