using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Обязательные платежи (Задача 3): план регулярных трат, факты оплаты
/// и сводка для виджета дашборда. В бюджет попадают только созданные операции.
/// </summary>
[ApiController]
[Route("api/obligations")]
[Authorize]
public class ObligationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ObligationService _obligations;
    public ObligationsController(AppDbContext db, ObligationService obligations)
    {
        _db = db;
        _obligations = obligations;
    }
    private int Uid => UserAccess.UidOf(User);

    public record ObligationDto(
        string? Name, string? Payee, int? CategoryId, decimal Amount,
        int Periodicity, DateOnly? NextPaymentDate, int? PaymentDay,
        int? IntervalDays, int? AccountId, bool Remind, int? DebtId, string? Note);

    public record PaymentDto(
        decimal? Amount, DateOnly? Date, string? Note, bool CreateTransaction,
        int? AccountId, bool ReduceDebt = false);

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    /// <summary>Проекция платежа с историей оплат (для списка, карточки и ответов).</summary>
    private static object Project(Obligation o, DateOnly today) => new
    {
        o.Id,
        o.Name,
        o.Payee,
        o.CategoryId,
        categoryName = o.Category?.Name,
        categoryColor = o.Category?.Color,
        o.Amount,
        periodicity = (int)o.Periodicity,
        periodicityName = ObligationCalculator.PeriodicityName(o.Periodicity),
        nextPaymentDate = Iso(o.NextPaymentDate),
        o.PaymentDay,
        o.IntervalDays,
        o.AccountId,
        accountName = o.Account?.Name,
        o.Remind,
        o.DebtId,
        debtName = o.Debt?.Name,
        o.Note,
        status = (int)o.Status,
        statusName = ObligationCalculator.StatusName(o.Status, o.NextPaymentDate, today),
        isOverdue = ObligationCalculator.IsOverdue(o.Status, o.NextPaymentDate, today),
        daysLeft = o.NextPaymentDate.DayNumber - today.DayNumber,
        paymentsCount = o.Payments.Count,
        lastPaymentDate = o.Payments.Count > 0 ? Iso(o.Payments.Max(p => p.Date)) : null,
        payments = o.Payments.Select(ProjectPayment).ToList(),
    };

    private static object ProjectPayment(ObligationPayment p) => new
    {
        p.Id,
        p.Amount,
        date = Iso(p.Date),
        plannedDate = Iso(p.PlannedDate),
        p.Note,
        p.TransactionId,
        p.CreatedAt,
    };

    /// <summary>Список платежей: filter = active (по умолчанию), paused, completed, all.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? filter)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var list = await _obligations.ListAsync(Uid, filter);
        return Ok(list.Select(o => Project(o, today)).ToList());
    }

    /// <summary>Сводка по обязательным платежам для виджета дашборда.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var all = await _db.Obligations.Where(o => o.UserId == Uid).ToListAsync();
        var active = all.Where(o => o.Status == ObligationStatus.Active).ToList();

        var overdue = active.Where(o => o.NextPaymentDate < today).ToList();
        var soon = active.Where(o => o.NextPaymentDate >= today
            && o.NextPaymentDate <= today.AddDays(7)).ToList();
        var month = active.Where(o => o.NextPaymentDate.Year == today.Year
            && o.NextPaymentDate.Month == today.Month).ToList();

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        // SQLite не умеет SUM по decimal — суммируем после выборки (записей за месяц немного).
        var paidThisMonth = (await _db.ObligationPayments
            .Where(p => p.UserId == Uid && p.Date >= monthStart && p.Date < monthEnd)
            .Select(p => p.Amount)
            .ToListAsync()).Sum();

        var upcoming = active.OrderBy(o => o.NextPaymentDate).Take(5).ToList();
        var next = upcoming.FirstOrDefault();

        return Ok(new
        {
            activeCount = active.Count,
            pausedCount = all.Count(o => o.Status == ObligationStatus.Paused),
            completedCount = all.Count(o => o.Status == ObligationStatus.Completed),
            overdueCount = overdue.Count,
            overdueAmount = overdue.Sum(o => o.Amount),
            dueIn7DaysCount = soon.Count,
            dueIn7DaysAmount = soon.Sum(o => o.Amount),
            monthPlannedAmount = month.Sum(o => o.Amount),
            paidThisMonthAmount = paidThisMonth,
            next = next is null ? null : new
            {
                next.Id,
                next.Name,
                next.Payee,
                nextPaymentDate = Iso(next.NextPaymentDate),
                next.Amount,
                isOverdue = next.NextPaymentDate < today,
            },
            upcoming = upcoming.Select(o => new
            {
                o.Id,
                o.Name,
                nextPaymentDate = Iso(o.NextPaymentDate),
                o.Amount,
                isOverdue = o.NextPaymentDate < today,
            }).ToList(),
        });
    }

    /// <summary>Обязательный платёж с полной историей оплат.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var obligation = await _obligations.GetAsync(Uid, id);
        if (obligation is null) return NotFound(new { error = "Платёж не найден" });
        return Ok(Project(obligation, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Создать обязательный платёж.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ObligationDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error, obligation) = await _obligations.CreateAsync(Uid, ToInput(dto));
        if (!ok || obligation is null) return BadRequest(new { error });

        var created = await _obligations.GetAsync(Uid, obligation.Id);
        return Ok(Project(created!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Изменить обязательный платёж.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ObligationDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.UpdateAsync(Uid, id, ToInput(dto));
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Удалить платёж, историю оплат и связанные операции.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.DeleteAsync(Uid, id);
        if (!ok) return NotFound(new { error });
        return Ok(new { deleted = id });
    }

    /// <summary>Отметить оплату; операция по счёту создаётся по галочке.</summary>
    [HttpPost("{id:int}/pay")]
    public async Task<IActionResult> Pay(int id, [FromBody] PaymentDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var input = new ObligationService.PaymentInput(
            dto.Amount, dto.Date, dto.Note, dto.CreateTransaction, dto.AccountId, dto.ReduceDebt);
        var (ok, error) = await _obligations.PayAsync(Uid, id, input);
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Удалить факт оплаты (связанная операция тоже удаляется).</summary>
    [HttpDelete("{id:int}/payments/{paymentId:int}")]
    public async Task<IActionResult> DeletePayment(int id, int paymentId)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.DeletePaymentAsync(Uid, id, paymentId);
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Поставить платёж на паузу.</summary>
    [HttpPost("{id:int}/pause")]
    public async Task<IActionResult> Pause(int id) => await ChangeStatus(id, ObligationStatus.Paused);

    /// <summary>Возобновить платёж.</summary>
    [HttpPost("{id:int}/resume")]
    public async Task<IActionResult> Resume(int id) => await ChangeStatus(id, ObligationStatus.Active);

    /// <summary>Завершить платёж (без создания факта оплаты).</summary>
    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id) => await ChangeStatus(id, ObligationStatus.Completed);

    private async Task<IActionResult> ChangeStatus(int id, ObligationStatus status)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.SetStatusAsync(Uid, id, status);
        if (!ok) return NotFound(new { error });
        return Ok(new { id, status = (int)status });
    }

    private static ObligationService.ObligationInput ToInput(ObligationDto dto) =>
        new(dto.Name, dto.Payee, dto.CategoryId, dto.Amount, dto.Periodicity,
            dto.NextPaymentDate, dto.PaymentDay, dto.IntervalDays,
            dto.AccountId, dto.Remind, dto.DebtId, dto.Note);
}
