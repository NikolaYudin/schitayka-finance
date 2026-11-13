using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Доходность вкладов и накопительных счетов (Задача 5):
/// прогноз, журнал начислений и фиксация выплаты.
/// </summary>
[ApiController]
[Route("api/interest")]
[Authorize]
public class InterestController : ControllerBase
{
    private readonly AppDbContext _db;

    public InterestController(AppDbContext db) => _db = db;

    private int Uid => UserAccess.UidOf(User);

    public record AccrueDto(string? Note = null, DateTime? From = null, DateTime? To = null);
    public record PayoutDto(decimal Amount, DateOnly? Date = null, string? Note = null);

    /// <summary>Прогноз доходности счёта на N месяцев вперёд.</summary>
    [HttpGet("forecast/{accountId:int}")]
    public async Task<IActionResult> Forecast([FromRoute] int accountId, [FromQuery] int months = 12)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == Uid);
        if (acc is null) return NotFound(new { error = "Счёт не найден" });

        var f = InterestCalculator.Forecast(
            acc.InterestRate, acc.InterestMode, acc.Balance, months,
            acc.InterestTaxRate, acc.OpenedAt, acc.MaturesAt);

        return Ok(new
        {
            accountId = acc.Id,
            accountName = acc.Name,
            type = (int)acc.Type,
            typeName = AccountTypeNames.Ru(acc.Type),
            f.Rate,
            mode = (int)acc.InterestMode,
            modeName = AccountsController.InterestModeName(acc.InterestMode),
            f.Balance,
            f.Daily,
            f.MonthlyAvg,
            f.Yearly,
            f.GrossTotal,
            f.TaxTotal,
            f.NetTotal,
            f.Rows,
            f.Note,
            openedAt = acc.OpenedAt,
            maturesAt = acc.MaturesAt,
            daysToMaturity = acc.MaturesAt is { } m ? (m.Date - DateTime.Today).Days : (int?)null,
            taxRate = acc.InterestTaxRate,
        });
    }

    /// <summary>Журнал начислений по счетам пользователя.</summary>
    [HttpGet("accruals")]
    public async Task<IActionResult> Accruals([FromQuery] int? year = null, [FromQuery] int? accountId = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var q = _db.InterestAccruals.Where(a => a.UserId == Uid);
        if (accountId is int aid) q = q.Where(a => a.AccountId == aid);
        if (year is int y) q = q.Where(a => a.From.Year == y);

        var list = await q.OrderByDescending(a => a.From).ThenByDescending(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.AccountId,
                accountName = a.Account.Name,
                a.From,
                a.To,
                a.Days,
                a.Gross,
                a.Tax,
                a.Net,
                a.IsPaid,
                a.CreatedAt,
            })
            .ToListAsync();

        return Ok(list);
    }

    /// <summary>
    /// Начислить проценты за период (по умолчанию — с прошлого начисления по сегодня).
    /// Ничего не списывает: это только учёт в журнале.
    /// </summary>
    [HttpPost("accrue/{accountId:int}")]
    public async Task<IActionResult> Accrue([FromRoute] int accountId, [FromBody] AccrueDto? dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == Uid);
        if (acc is null) return NotFound(new { error = "Счёт не найден" });
        if (acc.InterestRate <= 0 || acc.InterestMode == InterestAccrualMode.None)
            return BadRequest(new { error = "У счёта не задана ставка" });

        var to = (dto?.To ?? DateTime.Today).Date;
        var last = await _db.InterestAccruals
            .Where(a => a.AccountId == acc.Id && a.UserId == Uid)
            .OrderByDescending(a => a.To)
            .Select(a => (DateTime?)a.To)
            .FirstOrDefaultAsync();
        var from = (dto?.From ?? last ?? acc.OpenedAt ?? to).Date;
        if (from >= to) return BadRequest(new { error = "Период начисления пуст" });

        var days = (to - from).Days;
        var gross = Math.Round(acc.Balance * acc.InterestRate / 100m * days / InterestCalculator.DaysInYear, 2);
        if (gross <= 0) return BadRequest(new { error = "За период начислять нечего" });

        var tax = InterestCalculator.TaxOf(gross, acc.InterestTaxRate);
        var row = new InterestAccrual
        {
            UserId = Uid,
            AccountId = acc.Id,
            From = from,
            To = to,
            Days = days,
            Gross = gross,
            Tax = tax,
            Net = gross - tax,
            IsPaid = false,
        };
        _db.InterestAccruals.Add(row);
        await _db.SaveChangesAsync();
        return Ok(new { row.Id, row.From, row.To, row.Days, row.Gross, row.Tax, row.Net, row.IsPaid });
    }

    /// <summary>
    /// Зафиксировать выплату процентов: создаёт доходную операцию на счёте
    /// и помечает начисления выплаченными.
    /// </summary>
    [HttpPost("payout/{accountId:int}")]
    public async Task<IActionResult> Payout([FromRoute] int accountId, [FromBody] PayoutDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма выплаты должна быть больше нуля" });

        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == Uid);
        if (acc is null) return NotFound(new { error = "Счёт не найден" });

        var date = dto.Date ?? DateOnly.FromDateTime(DateTime.Today);

        // Категория дохода для процентов — создаём системную при первом разе.
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.UserId == Uid && c.Name == "Проценты");
        if (cat is null)
        {
            cat = new Category { UserId = Uid, Name = "Проценты", Type = TxType.Income, Color = "#38a169", IsSystem = true };
            _db.Categories.Add(cat);
            await _db.SaveChangesAsync();
        }

        var tx = new Transaction
        {
            UserId = Uid,
            CategoryId = cat.Id,
            Type = TxType.Income,
            Amount = dto.Amount,
            Date = date,
            Comment = string.IsNullOrWhiteSpace(dto.Note) ? $"Проценты по счёту «{acc.Name}»" : dto.Note.Trim(),
            AccountId = acc.Id,
        };
        _db.Transactions.Add(tx);

        var pending = await _db.InterestAccruals
            .Where(a => a.AccountId == acc.Id && a.UserId == Uid && !a.IsPaid)
            .ToListAsync();
        foreach (var p in pending) p.IsPaid = true;

        await _db.SaveChangesAsync();

        // Пересчёт остатка счёта (старт + операции + кэшбэк) — как в AccountsController.
        await AccountsController.RecalcAccountsOf(_db, _db.Accounts.Where(a => a.Id == acc.Id));

        return Ok(new { tx.Id, tx.Amount, tx.Date, paidAccruals = pending.Count, tx.Comment });
    }
}
