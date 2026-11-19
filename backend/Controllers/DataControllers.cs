using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;
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
        await CategoryService.NormalizeFamilyAsync(_db, Uid);
        var list = await (await CategoryService.AccessibleAsync(_db, Uid))
            .OrderBy(c => c.Type).ThenBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.Type, c.Color, c.IsSystem,
                familyShared = c.FamilyCode != null,
            })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CategoryDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название обязательно" });
        await CategoryService.NormalizeFamilyAsync(_db, Uid);
        var accessible = await CategoryService.AccessibleAsync(_db, Uid);
        var exists = await accessible.AnyAsync(c => c.Name.Trim() == dto.Name.Trim() && c.Type == dto.Type);
        if (exists) return BadRequest(new { error = "Такая категория уже есть" });
        var cat = new Category
        {
            UserId = Uid,
            Name = dto.Name.Trim(),
            Type = dto.Type,
            Color = string.IsNullOrWhiteSpace(dto.Color) ? "#2b6cb0" : dto.Color,
            FamilyCode = await CategoryService.FamilyCodeOfAsync(_db, Uid),
        };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        return Ok(new { cat.Id, cat.Name, cat.Type, cat.Color, cat.IsSystem, familyShared = cat.FamilyCode != null });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CategoryDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        await CategoryService.NormalizeFamilyAsync(_db, Uid);
        var cat = await CategoryService.FindAsync(_db, Uid, id);
        if (cat is null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название обязательно" });
        var duplicate = await (await CategoryService.AccessibleAsync(_db, Uid))
            .AnyAsync(c => c.Id != id && c.Name.Trim() == dto.Name.Trim() && c.Type == dto.Type);
        if (duplicate) return BadRequest(new { error = "Такая категория уже есть" });
        cat.Name = dto.Name.Trim();
        cat.Type = dto.Type;
        if (!string.IsNullOrWhiteSpace(dto.Color)) cat.Color = dto.Color;
        await _db.SaveChangesAsync();
        return Ok(new { cat.Id, cat.Name, cat.Type, cat.Color, cat.IsSystem, familyShared = cat.FamilyCode != null });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        await CategoryService.NormalizeFamilyAsync(_db, Uid);
        var cat = await CategoryService.FindAsync(_db, Uid, id);
        if (cat is null) return NotFound();
        if (cat.IsSystem) return BadRequest(new { error = "Системную категорию удалить нельзя" });
        var used = await _db.Transactions.AnyAsync(t => t.CategoryId == id)
            || await _db.Obligations.AnyAsync(o => o.CategoryId == id)
            || await _db.AccountRules.AnyAsync(r => r.MatchCategoryId == id || r.SetCategoryId == id);
        if (used) return BadRequest(new { error = "Категория используется — удалите или перенесите записи" });
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
    private readonly SchitayKa.Api.Services.AccountRulesService _rules;
    public TransactionsController(AppDbContext db, SchitayKa.Api.Services.AccountRulesService rules)
    { _db = db; _rules = rules; }
    private int Uid => UserAccess.UidOf(User);

    public record TxDto(int CategoryId, TxType Type, decimal Amount, DateOnly Date, string? Comment, int? AccountId,
                        // Оставлено для обратной совместимости API, но значение игнорируется:
                        // признак перевода ставит только сервер (TransferService).
                        bool IsTransfer = false);

    // Пересчёт баланса счёта при изменении набора операций.
    // Дебетовые/наличные/вклад: старт + доходы − расходы (+ начисленный кэшбэк).
    // Кредитные: старт = долг; покупки увеличивают долг, погашения — уменьшают.
    private Task RecalcAccounts(params int?[] accountIds) => RecalcAccountsFor(Uid, accountIds);

    // Публичный вариант: пересчёт для произвольного пользователя (используется для семейных операций)
    public async Task RecalcAccountsFor(int userId, params int?[] accountIds)
    {
        var ids = accountIds.Where(a => a.HasValue).Select(a => a!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        await AccountBalanceService.RecalcAsync(_db,
            _db.Accounts.Where(a => ids.Contains(a.Id) && a.UserId == userId));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int month = 0, [FromQuery] int year = 0,
                                         [FromQuery] int take = 100)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var q = _db.Transactions.Where(t => t.UserId == Uid);
        if (month > 0 && year > 0)
            q = q.Where(t => t.Date.Month == month && t.Date.Year == year);

        // Операции, входящие в перевод, показываем отдельным блоком «Переводы»,
        // чтобы не было дублей в журнале операций.
        var transferTxIds = await new TransferService(_db).TransferTransactionIdsAsync(Uid);
        if (transferTxIds.Count > 0)
        {
            var ids = transferTxIds.ToList();
            q = q.Where(t => !ids.Contains(t.Id));
        }

        var list = await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(t => new
            {
                t.Id, t.CategoryId, t.Type, t.Amount, t.Date, t.Comment, t.AccountId, t.IsTransfer,
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

        // 0.1: автоподстановка счёта и (опционально) категории по правилам.
        var match = await _rules.MatchAssignmentAsync(Uid,
            new SchitayKa.Api.Services.AccountRulesService.RuleCandidate(
                dto.Type, dto.Amount, dto.Comment, dto.CategoryId));
        var effectiveCategoryId = dto.CategoryId;
        if (match?.SetCategoryId is { } setCat
            && await CategoryService.FindAsync(_db, Uid, setCat) is not null)
            effectiveCategoryId = setCat;
        var effectiveAccountId = dto.AccountId ?? match?.AccountId;

        var cat = await CategoryService.FindAsync(_db, Uid, effectiveCategoryId);
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (effectiveAccountId is { } accId && !await _db.Accounts.AnyAsync(a => a.Id == accId && a.UserId == Uid && !a.IsArchived))
            return BadRequest(new { error = "Счёт не найден или находится в архиве" });

        var tx = new Transaction
        {
            UserId = Uid,
            CategoryId = cat.Id,
            Type = dto.Type,
            Amount = dto.Amount,
            Date = dto.Date,
            Comment = dto.Comment?.Trim() ?? "",
            AccountId = effectiveAccountId,
            // IsTransfer из клиента игнорируем: иначе любую операцию можно вывести
            // из доходов/расходов и расчёта кэшбэка. Переводы создаёт TransferService.
            IsTransfer = false,
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        await RecalcAccounts(dto.AccountId, effectiveAccountId);
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok(new { tx.Id, tx.CategoryId, tx.Type, tx.Amount, tx.Date, tx.Comment, tx.AccountId, tx.IsTransfer,
            appliedRuleId = match?.RuleId });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, TxDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == Uid);
        if (tx is null) return NotFound();
        var cat = await CategoryService.FindAsync(_db, Uid, dto.CategoryId);
        // B20: исторические пары IsTransfer могут не иметь записи Transfers —
        // защищаем и их, иначе редактирование одной половины разбалансирует перевод.
        if (tx.IsTransfer || await _db.Transfers.AnyAsync(t => t.FromTxId == id || t.ToTxId == id))
            return BadRequest(new { error = "Стороны перевода нельзя изменять отдельно. Отмените перевод и создайте его заново." });
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма должна быть больше нуля" });
        if (dto.AccountId is { } accId && accId != tx.AccountId
            && !await _db.Accounts.AnyAsync(a => a.Id == accId && a.UserId == Uid && !a.IsArchived))
            return BadRequest(new { error = "Счёт не найден или находится в архиве" });
        var oldAccountId = tx.AccountId;
        tx.CategoryId = cat.Id; tx.Type = dto.Type; tx.Amount = dto.Amount;
        tx.Date = dto.Date; tx.Comment = dto.Comment?.Trim() ?? ""; tx.AccountId = dto.AccountId;
        // Признак перевода не меняется клиентом: его выставляет только TransferService.
        await _db.SaveChangesAsync();
        await RecalcAccounts(oldAccountId, dto.AccountId);
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok(new { tx.Id, tx.CategoryId, tx.Type, tx.Amount, tx.Date, tx.Comment, tx.AccountId, tx.IsTransfer });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == Uid);
        if (tx is null) return NotFound();
        var accId = tx.AccountId;

        // Операция входит в перевод — отменяем перевод целиком (обе стороны),
        // иначе суммы по счетам разойдутся.
        var result = await new TransferService(_db).CancelByTransactionDetailedAsync(Uid, id);
        if (result.Error is not null)
            return BadRequest(new { error = result.Error });
        var canceledTransferId = result.TransferId;

        if (canceledTransferId is null)
        {
            // Одиночная операция: удаляем и пересчитываем её счёт.
            _db.Transactions.Remove(tx);
            await _db.SaveChangesAsync();
            await RecalcAccounts(accId);
        }
        // B20: при отмене перевода TransferService уже удалил обе половины
        // и пересчитал оба счёта — повторный пересчёт здесь не нужен.
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok(new { canceledTransferId });
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
        var cat = await CategoryService.FindAsync(_db, Uid, dto.CategoryId);
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
