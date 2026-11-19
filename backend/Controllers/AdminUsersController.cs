using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Управление пользователями семьи. Проект рассчитан на одну семью на инсталляцию,
/// поэтому мастер видит только свою семью и созданных им пользователей (B7-замена).
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Master")]
public class AdminUsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public AdminUsersController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    private int Uid => UserAccess.UidOf(User);

    public record CreateUserDto(string Email, string Name, string Password);
    public record ResetPasswordDto(string Password);

    private async Task<IQueryable<User>> VisibleAsync()
    {
        var me = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Uid);
        var code = me?.FamilyCode;
        return _db.Users.Where(u => !u.IsSystemMaster &&
            (u.CreatedByUserId == Uid || (code != null && u.FamilyCode == code)));
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var me = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Uid);
        if (me is null) return Unauthorized();
        var code = me.FamilyCode;
        var rows = await _db.Users.AsNoTracking()
            .Where(u => !u.IsSystemMaster &&
                (u.CreatedByUserId == Uid || (code != null && u.FamilyCode == code)))
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id, u.Email, u.Name,
                role = u.Role.ToString(),
                hasPassword = !string.IsNullOrEmpty(u.PasswordHash),
                isFamily = code != null && u.FamilyCode == code,
                u.CreatedAt,
            })
            .ToListAsync();
        return Ok(rows);
    }

    /// <summary>Создать пользователя и сразу включить его в семью администратора.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateUserDto dto)
    {
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

        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Id == Uid);
        if (admin is null) return Unauthorized();

        var user = new User
        {
            Email = email,
            Name = name,
            PasswordHash = AuthController.HashPassword(password),
            Role = UserRole.Member,           // администратор один — мастер, остальные участники
            FamilyCode = admin.FamilyCode ?? "FAM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            CreatedByUserId = Uid,
            Theme = ThemeSettings.System,
        };
        if (admin.FamilyCode is null) admin.FamilyCode = user.FamilyCode;
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        SeedCategories.SeedForUser(_db, user.Id);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Uid, "admin.user-created", $"создан пользователь {email}", HttpContext);

        return Ok(new { user.Id, user.Email, user.Name, role = user.Role.ToString() });
    }

    /// <summary>Сброс пароля: все активные сессии пользователя становятся недействительными.</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordDto dto)
    {
        var visible = await VisibleAsync();
        var user = await visible.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();
        if (user.Id == Uid) return BadRequest(new { error = "Смените свой пароль в профиле" });
        var password = dto.Password ?? "";
        if (password.Length < 8)
            return BadRequest(new { error = "Пароль должен быть не короче 8 символов" });

        user.PasswordHash = AuthController.HashPassword(password);
        user.PasswordVersion++;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Uid, "admin.password-reset", $"сброшен пароль пользователя #{id}", HttpContext);
        return Ok(new { changed = true });
    }
}
