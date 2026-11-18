using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Долги (Задача 2): обязательства «я должен» и «мне должны» с историей событий.
/// Учитываются отдельно от доходов/расходов бюджета.
/// </summary>
[ApiController]
[Route("api/debts")]
[Authorize]
public class DebtsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DebtService _debts;
    public DebtsController(AppDbContext db, DebtService debts)
    {
        _db = db;
        _debts = debts;
    }
    private int Uid => UserAccess.UidOf(User);

    public record DebtDto(
        int Direction, string? Counterparty, string? Name, decimal PrincipalAmount,
        string? Currency, DateOnly? IssuedAt, DateOnly? DueDate, decimal? InterestRate,
        string? Terms, int? AccountId, string? Note);

    public record DebtEventDto(
        int Kind, decimal Amount, DateOnly? Date, string? Note,
        bool CreateTransaction, int? AccountId);

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    /// <summary>Проекция долга с историей событий (для списка, карточки и ответов).</summary>
    private static object Project(Debt d, DateOnly today) => new
    {
        d.Id,
        direction = (int)d.Direction,
        directionName = DebtCalculator.DirectionName(d.Direction),
        d.Counterparty,
        d.Name,
        d.PrincipalAmount,
        d.RemainingAmount,
        d.Currency,
        issuedAt = Iso(d.IssuedAt),
        dueDate = Iso(d.DueDate),
        d.InterestRate,
        d.Terms,
        d.AccountId,
        accountName = d.Account?.Name,
        d.Note,
        status = (int)d.Status,
        statusName = DebtCalculator.StatusName(d.Status, d.RemainingAmount, d.DueDate, today),
        isOverdue = DebtCalculator.IsOverdue(d.Status, d.RemainingAmount, d.DueDate, today),
        daysLeft = d.DueDate is DateOnly due ? due.DayNumber - today.DayNumber : (int?)null,
        eventsCount = d.Events.Count,
        d.CreatedAt,
        events = d.Events.Select(ProjectEvent).ToList(),
        // Связанный график платежей (Задача 4): только активные, без учёта сумм в аналитике.
        linkedObligations = d.Obligations
            .Where(o => o.Status == ObligationStatus.Active)
            .OrderBy(o => o.NextPaymentDate)
            .Select(o => new
            {
                o.Id,
                o.Name,
                o.Amount,
                nextPaymentDate = Iso(o.NextPaymentDate),
                statusName = ObligationCalculator.StatusName(o.Status, o.NextPaymentDate, today),
                isOverdue = ObligationCalculator.IsOverdue(o.Status, o.NextPaymentDate, today),
            }).ToList(),
    };

    private static object ProjectEvent(DebtEvent e) => new
    {
        e.Id,
        kind = (int)e.Kind,
        kindName = DebtCalculator.KindName(e.Kind),
        e.Amount,
        date = Iso(e.Date),
        e.Note,
        e.TransactionId,
        e.CreatedAt,
    };

    /// <summary>Список долгов: filter = active (по умолчанию), closed, all.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? filter)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var list = await _debts.ListAsync(Uid, filter);
        return Ok(list.Select(d => Project(d, today)).ToList());
    }

    /// <summary>Сводка по долгам для виджета дашборда.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var all = await _db.Debts.Where(d => d.UserId == Uid).ToListAsync();
        var active = all.Where(d => d.Status == DebtStatus.Active && d.RemainingAmount > 0).ToList();

        var iOwe = active.Where(d => d.Direction == DebtDirection.IOwe).Sum(d => d.RemainingAmount);
        var owedToMe = active.Where(d => d.Direction == DebtDirection.OwedToMe).Sum(d => d.RemainingAmount);
        var overdue = active
            .Where(d => DebtCalculator.IsOverdue(d.Status, d.RemainingAmount, d.DueDate, today))
            .ToList();
        var nearest = active.Where(d => d.DueDate is not null)
            .OrderBy(d => d.DueDate)
            .FirstOrDefault();

        return Ok(new
        {
            iOwe,
            owedToMe,
            net = owedToMe - iOwe,
            activeCount = active.Count,
            closedCount = all.Count(d => d.Status == DebtStatus.Closed || d.RemainingAmount <= 0),
            overdueCount = overdue.Count,
            overdueAmount = overdue.Sum(d => d.RemainingAmount),
            nearest = nearest is null ? null : new
            {
                nearest.Id,
                nearest.Name,
                dueDate = Iso(nearest.DueDate),
                nearest.RemainingAmount,
            },
        });
    }

    /// <summary>Долг с полной историей событий.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var debt = await _debts.GetAsync(Uid, id);
        if (debt is null) return NotFound(new { error = "Долг не найден" });

        var today = DateOnly.FromDateTime(DateTime.Today);
        return Ok(Project(debt, today));
    }

    /// <summary>Создать долг.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DebtDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var input = new DebtService.DebtInput(
            dto.Direction, dto.Counterparty, dto.Name, dto.PrincipalAmount,
            dto.Currency, dto.IssuedAt, dto.DueDate, dto.InterestRate ?? 0m,
            dto.Terms, dto.AccountId, dto.Note);
        var (ok, error, debt) = await _debts.CreateAsync(Uid, input);
        if (!ok || debt is null) return BadRequest(new { error });

        await _db.Entry(debt).Reference(d => d.Account).LoadAsync();
        await _db.Entry(debt).Collection(d => d.Events).LoadAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Ok(Project(debt, today));
    }

    /// <summary>Изменить долг.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DebtDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var input = new DebtService.DebtInput(
            dto.Direction, dto.Counterparty, dto.Name, dto.PrincipalAmount,
            dto.Currency, dto.IssuedAt, dto.DueDate, dto.InterestRate ?? 0m,
            dto.Terms, dto.AccountId, dto.Note);
        var (ok, error) = await _debts.UpdateAsync(Uid, id, input);
        if (!ok) return BadRequest(new { error });

        var debt = await _debts.GetAsync(Uid, id);
        if (debt is null) return NotFound(new { error = "Долг не найден" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Ok(Project(debt, today));
    }

    /// <summary>Удалить долг вместе с историей и связанными операциями.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _debts.DeleteAsync(Uid, id);
        if (!ok) return NotFound(new { error });
        return Ok(new { deleted = id });
    }

    /// <summary>Добавить событие истории (возврат, проценты, корректировка, списание).</summary>
    [HttpPost("{id:int}/events")]
    public async Task<IActionResult> AddEvent(int id, [FromBody] DebtEventDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var input = new DebtService.DebtEventInput(
            dto.Kind, dto.Amount, dto.Date, dto.Note, dto.CreateTransaction, dto.AccountId);
        var (ok, error) = await _debts.AddEventAsync(Uid, id, input);
        if (!ok) return BadRequest(new { error });

        var debt = await _debts.GetAsync(Uid, id);
        if (debt is null) return NotFound(new { error = "Долг не найден" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Ok(Project(debt, today));
    }

    /// <summary>Удалить событие истории (связанная операция тоже удаляется).</summary>
    [HttpDelete("{id:int}/events/{eventId:int}")]
    public async Task<IActionResult> DeleteEvent(int id, int eventId)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _debts.DeleteEventAsync(Uid, id, eventId);
        if (!ok) return BadRequest(new { error });

        var debt = await _debts.GetAsync(Uid, id);
        if (debt is null) return NotFound(new { error = "Долг не найден" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Ok(Project(debt, today));
    }

    /// <summary>Закрыть долг вручную.</summary>
    [HttpPost("{id:int}/close")]
    public async Task<IActionResult> Close(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _debts.SetStatusAsync(Uid, id, close: true);
        if (!ok) return NotFound(new { error });
        return Ok(new { id, status = (int)DebtStatus.Closed });
    }

    /// <summary>Снова открыть закрытый долг.</summary>
    [HttpPost("{id:int}/reopen")]
    public async Task<IActionResult> Reopen(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _debts.SetStatusAsync(Uid, id, close: false);
        if (!ok) return BadRequest(new { error });
        return Ok(new { id, status = (int)DebtStatus.Active });
    }
}
