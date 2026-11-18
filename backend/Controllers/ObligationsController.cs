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

    /// <summary>Охват «я / семья / выбранные» для обязательных платежей.</summary>
    private async Task<List<int>> ScopeIdsAsync(string? scope, string? ids) =>
        (await FamilyAccess.ResolveScopeAsync(_db, scope, ids, Uid)).UserIds;

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
        ownerId = o.UserId,
        ownerName = o.User?.Name,
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
    public async Task<IActionResult> List(
        [FromQuery] string? filter, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var list = await _obligations.ListAsync(Uid, filter, await ScopeIdsAsync(scope, ids));
        return Ok(list.Select(o => Project(o, today)).ToList());
    }

    /// <summary>Сводка по обязательным платежам для виджета дашборда.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var all = await _db.Obligations.Where(o => scopeIds.Contains(o.UserId)).ToListAsync();
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
            .Where(p => scopeIds.Contains(p.UserId) && p.Date >= monthStart && p.Date < monthEnd)
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
    public async Task<IActionResult> Get(int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var obligation = await _obligations.GetAsync(Uid, id, await ScopeIdsAsync(scope, ids));
        if (obligation is null) return NotFound(new { error = "Платёж не найден" });
        return Ok(Project(obligation, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Создать обязательный платёж.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] ObligationDto dto, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error, obligation) = await _obligations.CreateAsync(Uid, ToInput(dto), scopeIds);
        if (!ok || obligation is null) return BadRequest(new { error });

        var created = await _obligations.GetAsync(Uid, obligation.Id, scopeIds);
        return Ok(Project(created!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Изменить обязательный платёж.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id, [FromBody] ObligationDto dto, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error) = await _obligations.UpdateAsync(Uid, id, ToInput(dto), scopeIds);
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id, scopeIds);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Удалить платёж, историю оплат и связанные операции.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.DeleteAsync(Uid, id, await ScopeIdsAsync(scope, ids));
        if (!ok) return NotFound(new { error });
        return Ok(new { deleted = id });
    }

    /// <summary>Отметить оплату; операция по счёту создаётся по галочке.</summary>
    [HttpPost("{id:int}/pay")]
    public async Task<IActionResult> Pay(
        int id, [FromBody] PaymentDto dto, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var input = new ObligationService.PaymentInput(
            dto.Amount, dto.Date, dto.Note, dto.CreateTransaction, dto.AccountId, dto.ReduceDebt);
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error) = await _obligations.PayAsync(Uid, id, input, scopeIds);
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id, scopeIds);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Удалить факт оплаты (связанная операция тоже удаляется).</summary>
    [HttpDelete("{id:int}/payments/{paymentId:int}")]
    public async Task<IActionResult> DeletePayment(
        int id, int paymentId, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error) = await _obligations.DeletePaymentAsync(Uid, id, paymentId, scopeIds);
        if (!ok) return BadRequest(new { error });

        var obligation = await _obligations.GetAsync(Uid, id, scopeIds);
        return Ok(Project(obligation!, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Поставить платёж на паузу.</summary>
    [HttpPost("{id:int}/pause")]
    public async Task<IActionResult> Pause(
        int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null) =>
        await ChangeStatus(id, ObligationStatus.Paused, scope, ids);

    /// <summary>Возобновить платёж.</summary>
    [HttpPost("{id:int}/resume")]
    public async Task<IActionResult> Resume(
        int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null) =>
        await ChangeStatus(id, ObligationStatus.Active, scope, ids);

    /// <summary>Завершить платёж (без создания факта оплаты).</summary>
    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(
        int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null) =>
        await ChangeStatus(id, ObligationStatus.Completed, scope, ids);

    private async Task<IActionResult> ChangeStatus(
        int id, ObligationStatus status, string? scope, string? ids)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _obligations.SetStatusAsync(
            Uid, id, status, await ScopeIdsAsync(scope, ids));
        if (!ok) return NotFound(new { error });
        return Ok(new { id, status = (int)status });
    }

    private static ObligationService.ObligationInput ToInput(ObligationDto dto) =>
        new(dto.Name, dto.Payee, dto.CategoryId, dto.Amount, dto.Periodicity,
            dto.NextPaymentDate, dto.PaymentDay, dto.IntervalDays,
            dto.AccountId, dto.Remind, dto.DebtId, dto.Note);
}
