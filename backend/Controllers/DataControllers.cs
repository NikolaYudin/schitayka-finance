using System.Security.Claims;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Controllers;

public static class UserAccess
{
    // Безопасное чтение id пользователя из JWT (без 500 при пустом токене)
    public static bool TryUid(ClaimsPrincipal user, out int uid) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out uid);

    public static int UidOf(ClaimsPrincipal user) => TryUid(user, out var uid) ? uid : -1;
}

[ApiController]
[Route("api/categories")]
[Authorize]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CategoriesController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record CategoryDto(string Name, TxType Type, string? Color);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _db.Categories.Where(c => c.UserId == Uid)
            .OrderBy(c => c.Type).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Type, c.Color, c.IsSystem })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CategoryDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название обязательно" });
        var exists = await _db.Categories.AnyAsync(c => c.UserId == Uid && c.Name == dto.Name.Trim() && c.Type == dto.Type);
        if (exists) return BadRequest(new { error = "Такая категория уже есть" });
        var cat = new Category
        {
            UserId = Uid,
            Name = dto.Name.Trim(),
            Type = dto.Type,
            Color = string.IsNullOrWhiteSpace(dto.Color) ? "#2b6cb0" : dto.Color,
        };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        return Ok(new { cat.Id, cat.Name, cat.Type, cat.Color, cat.IsSystem });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CategoryDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == Uid);
        if (cat is null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название обязательно" });
        cat.Name = dto.Name.Trim();
        cat.Type = dto.Type;
        if (!string.IsNullOrWhiteSpace(dto.Color)) cat.Color = dto.Color;
        await _db.SaveChangesAsync();
        return Ok(new { cat.Id, cat.Name, cat.Type, cat.Color, cat.IsSystem });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == Uid);
        if (cat is null) return NotFound();
        var used = await _db.Transactions.AnyAsync(t => t.CategoryId == id);
        if (used) return BadRequest(new { error = "Категория используется в операциях — удалите или перенесите их" });
        _db.Categories.Remove(cat);
        await _db.SaveChangesAsync();
        return Ok();
    }
}

[ApiController]
[Route("api/transactions")]
[Authorize]
public class TransactionsController : ControllerBase
{
    private readonly AppDbContext _db;
    public TransactionsController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record TxDto(int CategoryId, TxType Type, decimal Amount, DateOnly Date, string? Comment, int? AccountId);

    // Пересчёт баланса счёта при изменении набора операций.
    // Дебетовые/наличные/вклад: старт + доходы − расходы (+ начисленный кэшбэк).
    // Кредитные: старт = долг; покупки увеличивают долг, погашения — уменьшают.
    private async Task RecalcAccounts(params int?[] accountIds)
    {
        foreach (var id in accountIds.Where(a => a.HasValue).Select(a => a!.Value).Distinct())
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
            if (acc is null) continue;
            var signed = await _db.Transactions.Where(t => t.AccountId == id)
                .Select(t => new { t.Type, t.Amount }).ToListAsync();
            var net = signed.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
            if (acc.Type == AccountType.CreditCard)
            {
                acc.Balance = Math.Max(0, acc.StartingBalance - net);
                acc.CashbackAccrued = 0;
            }
            else
            {
                var cashback = acc.CashbackRate > 0
                    ? Math.Round(signed.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount) * acc.CashbackRate / 100m, 2)
                    : 0m;
                acc.Balance = Math.Max(0, acc.StartingBalance + net + cashback);
                acc.CashbackAccrued = cashback;
            }
        }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int month = 0, [FromQuery] int year = 0,
                                         [FromQuery] int take = 100)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var q = _db.Transactions.Where(t => t.UserId == Uid);
        if (month > 0 && year > 0)
            q = q.Where(t => t.Date.Month == month && t.Date.Year == year);
        var list = await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(t => new
            {
                t.Id, t.CategoryId, t.Type, t.Amount, t.Date, t.Comment, t.AccountId,
                categoryName = t.Category.Name, categoryColor = t.Category.Color,
                accountName = t.Account!.Name,
            })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create(TxDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма должна быть больше нуля" });
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.UserId == Uid);
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (dto.AccountId is { } accId && !await _db.Accounts.AnyAsync(a => a.Id == accId && a.UserId == Uid))
            return BadRequest(new { error = "Счёт не найден" });

        var tx = new Transaction
        {
            UserId = Uid,
            CategoryId = cat.Id,
            Type = dto.Type,
            Amount = dto.Amount,
            Date = dto.Date,
            Comment = dto.Comment?.Trim() ?? "",
            AccountId = dto.AccountId,
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        await RecalcAccounts(dto.AccountId);
        await _db.SaveChangesAsync();
        return Ok(new { tx.Id, tx.CategoryId, tx.Type, tx.Amount, tx.Date, tx.Comment, tx.AccountId });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, TxDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == Uid);
        if (tx is null) return NotFound();
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.UserId == Uid);
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма должна быть больше нуля" });
        if (dto.AccountId is { } accId && !await _db.Accounts.AnyAsync(a => a.Id == accId && a.UserId == Uid))
            return BadRequest(new { error = "Счёт не найден" });
        var oldAccountId = tx.AccountId;
        tx.CategoryId = cat.Id; tx.Type = dto.Type; tx.Amount = dto.Amount;
        tx.Date = dto.Date; tx.Comment = dto.Comment?.Trim() ?? ""; tx.AccountId = dto.AccountId;
        await _db.SaveChangesAsync();
        await RecalcAccounts(oldAccountId, dto.AccountId);
        await _db.SaveChangesAsync();
        return Ok(new { tx.Id, tx.CategoryId, tx.Type, tx.Amount, tx.Date, tx.Comment, tx.AccountId });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == Uid);
        if (tx is null) return NotFound();
        var accId = tx.AccountId;
        _db.Transactions.Remove(tx);
        await _db.SaveChangesAsync();
        await RecalcAccounts(accId);
        await _db.SaveChangesAsync();
        return Ok();
    }
}

[ApiController]
[Route("api/goals")]
[Authorize]
public class GoalsController : ControllerBase
{
    private readonly AppDbContext _db;
    public GoalsController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record GoalDto(string Name, decimal TargetAmount, DateOnly? Deadline);
    public record DepositDto(decimal Delta);

    private static object Project(SavingsGoal g)
    {
        // Персональный расчёт: сколько нужно откладывать, чтобы успеть к сроку
        decimal? perMonth = null;
        int? monthsLeft = null;
        if (g.Deadline is { } dl)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var remaining = Math.Max(0, g.TargetAmount - g.CurrentAmount);
            monthsLeft = (dl.Year - today.Year) * 12 + (dl.Month - today.Month)
                         + (dl.Day >= today.Day ? 0 : -1) + 1;
            if (monthsLeft < 0) monthsLeft = 0;
            perMonth = monthsLeft > 0 ? Math.Round(remaining / monthsLeft.Value, 0) : remaining;
        }
        return new
        {
            g.Id, g.Name, g.TargetAmount, g.CurrentAmount, g.Deadline,
            progress = g.TargetAmount > 0 ? Math.Round(g.CurrentAmount / g.TargetAmount * 100, 1) : 0,
            monthsLeft,
            perMonth,
        };
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var goals = await _db.SavingsGoals.Where(g => g.UserId == Uid).OrderBy(g => g.Deadline).ToListAsync();
        return Ok(goals.Select(Project));
    }

    [HttpPost]
    public async Task<IActionResult> Create(GoalDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.TargetAmount <= 0)
            return BadRequest(new { error = "Укажите название и целевую сумму" });
        var g = new SavingsGoal
        {
            UserId = Uid,
            Name = dto.Name.Trim(),
            TargetAmount = dto.TargetAmount,
            Deadline = dto.Deadline,
        };
        _db.SavingsGoals.Add(g);
        await _db.SaveChangesAsync();
        return Ok(Project(g));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, GoalDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var g = await _db.SavingsGoals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Uid);
        if (g is null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.TargetAmount <= 0)
            return BadRequest(new { error = "Укажите название и целевую сумму" });
        g.Name = dto.Name.Trim();
        g.TargetAmount = dto.TargetAmount;
        g.Deadline = dto.Deadline;
        await _db.SaveChangesAsync();
        return Ok(Project(g));
    }

    [HttpPost("{id:int}/deposit")]
    public async Task<IActionResult> Deposit(int id, DepositDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var g = await _db.SavingsGoals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Uid);
        if (g is null) return NotFound();
        g.CurrentAmount = Math.Max(0, g.CurrentAmount + dto.Delta);
        await _db.SaveChangesAsync();
        return Ok(Project(g));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var g = await _db.SavingsGoals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Uid);
        if (g is null) return NotFound();
        _db.SavingsGoals.Remove(g);
        await _db.SaveChangesAsync();
        return Ok();
    }
}

[ApiController]
[Route("api/limits")]
[Authorize]
public class LimitsController : ControllerBase
{
    private readonly AppDbContext _db;
    public LimitsController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record LimitDto(int CategoryId, int Year, int Month, decimal Limit);

    // Лимиты на месяц (year+month в query); если не указаны — текущий месяц
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year = 0, [FromQuery] int month = 0)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (year <= 0 || month <= 0) { year = DateTime.Today.Year; month = DateTime.Today.Month; }
        var list = await _db.BudgetLimits
            .Where(l => l.UserId == Uid && l.Year == year && l.Month == month)
            .Select(l => new { l.Id, l.CategoryId, l.Year, l.Month, l.Limit, categoryName = l.Category.Name })
            .ToListAsync();
        return Ok(list);
    }

    // Создать/обновить лимит (upsert по категории и месяцу)
    [HttpPost]
    public async Task<IActionResult> Upsert(LimitDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (dto.Month is < 1 or > 12 || dto.Year < 2000) return BadRequest(new { error = "Некорректный месяц/год" });
        if (dto.Limit < 0) return BadRequest(new { error = "Лимит не может быть отрицательным" });
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.UserId == Uid);
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (cat.Type != TxType.Expense) return BadRequest(new { error = "Лимиты задаются только для расходов" });

        var lim = await _db.BudgetLimits.FirstOrDefaultAsync(l =>
            l.UserId == Uid && l.CategoryId == dto.CategoryId && l.Year == dto.Year && l.Month == dto.Month);
        if (lim is null)
        {
            lim = new BudgetLimit { UserId = Uid, CategoryId = cat.Id, Year = dto.Year, Month = dto.Month, Limit = dto.Limit };
            _db.BudgetLimits.Add(lim);
        }
        else lim.Limit = dto.Limit;
        await _db.SaveChangesAsync();
        return Ok(new { lim.Id, lim.CategoryId, lim.Year, lim.Month, lim.Limit });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var lim = await _db.BudgetLimits.FirstOrDefaultAsync(l => l.Id == id && l.UserId == Uid);
        if (lim is null) return NotFound();
        _db.BudgetLimits.Remove(lim);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
