using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Первичная настройка (мастер-профиль в БД). Доступна, пока не создан ни один
/// реальный администратор. Мастер-профиль — служебная запись без пароля: при
/// настройке она превращается в первого пользователя с ролью Master.
/// </summary>
[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IConfiguration _cfg;

    public SetupController(AppDbContext db, AuditService audit, IConfiguration cfg)
    {
        _db = db;
        _audit = audit;
        _cfg = cfg;
    }

    public record AdminDto(string Email, string Name, string Password);

    /// <summary>Требуется ли первичная настройка.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var required = !await _db.Users.AnyAsync(u => u.Role == UserRole.Master && !u.IsSystemMaster);
        return Ok(new { required });
    }

    /// <summary>Создать первого администратора из мастер-профиля.</summary>
    [HttpPost("admin")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CreateAdmin(AdminDto dto)
    {
        if (await _db.Users.AnyAsync(u => u.Role == UserRole.Master && !u.IsSystemMaster))
            return Conflict(new { error = "Администратор уже создан" });

        var email = (dto.Email ?? "").Trim().ToLowerInvariant();
        var name = (dto.Name ?? "").Trim();
        var password = dto.Password ?? "";
        if (email.Length < 5 || !email.Contains('@'))
            return BadRequest(new { error = "Укажите корректный email" });
        if (name.Length == 0)
            return BadRequest(new { error = "Укажите имя" });
        if (password.Length < 8)
            return BadRequest(new { error = "Пароль должен быть не короче 8 символов" });
        if (await _db.Users.AnyAsync(u => u.Email == email))
            return BadRequest(new { error = "Пользователь с таким email уже существует" });

        // Мастер-профиль передаёт права: обновляем служебную запись, а не создаём вторую.
        var master = await _db.Users.FirstOrDefaultAsync(u => u.IsSystemMaster);
        var admin = master ?? new User();
        admin.Email = email;
        admin.Name = name;
        admin.PasswordHash = AuthController.HashPassword(password);
        admin.PasswordVersion = 0;
        admin.Role = UserRole.Master;
        admin.IsSystemMaster = false;
        admin.Theme = ThemeSettings.System;
        admin.FamilyCode ??= "FAM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        if (master is null) _db.Users.Add(admin);

        await _db.SaveChangesAsync();
        SeedCategories.SeedForUser(_db, admin.Id);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(admin.Id, "setup.admin-created",
            $"создан администратор {email}", HttpContext);

        // Сразу выдаём токен — пользователь попадает в приложение без повторного входа.
        var auth = new AuthController(_db, _cfg, _audit);
        return Ok(new { token = auth.BuildJwt(admin), user = new { admin.Id, admin.Email, admin.Name } });
    }
}
