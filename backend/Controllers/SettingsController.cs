using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>Настройки интерфейса, общие для всех устройств пользователя (B6).</summary>
[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    public SettingsController(AppDbContext db) => _db = db;

    private int Uid => UserAccess.UidOf(User);

    public record AppearanceDto(string Theme);

    [HttpGet("appearance")]
    public async Task<IActionResult> GetAppearance()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var theme = await _db.Users.Where(u => u.Id == Uid)
            .Select(u => u.Theme).FirstOrDefaultAsync();
        return Ok(new { theme = ThemeSettings.Normalize(theme) });
    }

    [HttpPut("appearance")]
    public async Task<IActionResult> SetAppearance(AppearanceDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var user = await _db.Users.FindAsync(Uid);
        if (user is null) return Unauthorized();
        user.Theme = ThemeSettings.Normalize(dto.Theme);
        await _db.SaveChangesAsync();
        return Ok(new { theme = user.Theme });
    }
}
