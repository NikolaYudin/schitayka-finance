using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Календарь событий (Задача 5): объединение дат по долгам, платежам, вкладам,
/// начислениям процентов и пользовательским событиям. Ни на что не влияет,
/// кроме собственного списка событий.
/// </summary>
[ApiController]
[Route("api/calendar")]
[Authorize]
public class CalendarController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CalendarService _calendar;
    public CalendarController(AppDbContext db, CalendarService calendar)
    {
        _db = db;
        _calendar = calendar;
    }
    private int Uid => UserAccess.UidOf(User);

    /// <summary>Охват «я / семья / выбранные» для календаря.</summary>
    private async Task<List<int>> ScopeIdsAsync(string? scope, string? ids) =>
        (await FamilyAccess.ResolveScopeAsync(_db, scope, ids, Uid)).UserIds;

    public record CalendarEventDto(
        string? Title, DateOnly? Date, string? Time, int Type, decimal? Amount,
        bool Remind, string? Note, int? DebtId, int? ObligationId, int? AccountId);

    private static object Project(CalendarItem i) => new
    {
        i.Key,
        i.Id,
        source = i.Source,
        sourceName = i.SourceName,
        i.Title,
        date = i.Date.ToString("yyyy-MM-dd"),
        i.Time,
        i.Amount,
        i.IsOverdue,
        i.DebtId,
        i.ObligationId,
        i.AccountId,
        i.Remind,
        i.Note,
        typeName = i.TypeName,
        ownerId = i.OwnerId,
        ownerName = i.OwnerName,
    };

    /// <summary>События в диапазоне дат (from/to — yyyy-MM-dd, types — список источников через запятую).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? types,
        [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var sources = ParseSources(types);
        var items = await _calendar.GetAsync(
            Uid, from ?? today, to ?? today.AddDays(30), sources, await ScopeIdsAsync(scope, ids));
        return Ok(items.Select(Project).ToList());
    }

    /// <summary>Ближайшие события для виджета дашборда.</summary>
    [HttpGet("upcoming")]
    public async Task<IActionResult> Upcoming(
        [FromQuery] int take = 5, [FromQuery] int days = 30,
        [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var items = await _calendar.UpcomingAsync(
            Uid, take, days, null, await ScopeIdsAsync(scope, ids));
        return Ok(items.Select(Project).ToList());
    }

    /// <summary>Создать пользовательское событие.</summary>
    [HttpPost("events")]
    public async Task<IActionResult> Create(
        [FromBody] CalendarEventDto dto, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error, entity) = await _calendar.CreateAsync(Uid, ToInput(dto), scopeIds);
        if (!ok || entity is null) return BadRequest(new { error });
        return Ok(Project(ToItem(entity)));
    }

    /// <summary>Изменить пользовательское событие.</summary>
    [HttpPut("events/{id:int}")]
    public async Task<IActionResult> Update(
        int id, [FromBody] CalendarEventDto dto, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var scopeIds = await ScopeIdsAsync(scope, ids);
        var (ok, error) = await _calendar.UpdateAsync(Uid, id, ToInput(dto), scopeIds);
        if (!ok) return BadRequest(new { error });
        var entity = await _calendar.GetEventAsync(Uid, id, scopeIds);
        return Ok(Project(ToItem(entity!)));
    }

    /// <summary>Удалить пользовательское событие.</summary>
    [HttpDelete("events/{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _calendar.DeleteAsync(
            Uid, id, await ScopeIdsAsync(scope, ids));
        if (!ok) return NotFound(new { error });
        return Ok(new { deleted = id });
    }

    private static CalendarItem ToItem(CalendarEvent e) => new(
        $"event:{e.Id}", e.Id, (int)CalendarSource.UserEvent, "Событие",
        e.Title, e.Date, e.Time, e.Amount, e.Date < DateOnly.FromDateTime(DateTime.Today),
        e.DebtId, e.ObligationId, e.AccountId, e.Remind, e.Note ?? "",
        e.Type switch
        {
            CalendarEventType.Reminder => "Напоминание",
            CalendarEventType.Meeting => "Встреча",
            CalendarEventType.Other => "Прочее",
            _ => "Событие",
        },
        e.UserId, null);

    private static CalendarService.CalendarEventInput ToInput(CalendarEventDto dto) =>
        new(dto.Title, dto.Date, dto.Time, dto.Type, dto.Amount,
            dto.Remind, dto.Note, dto.DebtId, dto.ObligationId, dto.AccountId);

    private static IReadOnlyCollection<int>? ParseSources(string? types)
    {
        if (string.IsNullOrWhiteSpace(types)) return null;
        var parsed = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : -1)
            .Where(v => v >= 0)
            .Distinct()
            .ToList();
        return parsed.Count > 0 ? parsed : null;
    }
}
