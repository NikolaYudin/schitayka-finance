using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Controllers;

/// <summary>Журнал чувствительных действий текущего пользователя (B10).</summary>
[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _db;
    public AuditController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 50)
    {
        if (!UserAccess.TryUid(User, out var uid)) return Unauthorized(new { error = "Требуется вход" });
        var items = await _db.AuditLogs
            .Where(a => a.UserId == uid)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(a => new
            {
                a.Id, a.Action, a.Details, a.IpAddress, a.CreatedAt,
            })
            .ToListAsync();
        return Ok(items);
    }
}
