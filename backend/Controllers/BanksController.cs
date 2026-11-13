using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Controllers;

/// <summary>Справочник банков, в которых открыт инвестиционный счёт (Задача 6).</summary>
[ApiController]
[Route("api/banks")]
[Authorize]
public class BanksController : ControllerBase
{
    private readonly AppDbContext _db;
    public BanksController(AppDbContext db) => _db = db;
    private int Uid => UserAccess.UidOf(User);

    public record BankDto(int Id, string Name, string Code, string Country, bool IsSystem);

    /// <summary>Системный справочник + собственные банки пользователя.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _db.Banks
            .Where(b => b.UserId == null || b.UserId == Uid)
            .OrderBy(b => b.IsSystem ? 0 : 1).ThenBy(b => b.Name)
            .Select(b => new BankDto(b.Id, b.Name, b.Code, b.Country, b.IsSystem))
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Добавить свой банк (если его нет в справочнике).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] BankDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название банка обязательно" });

        var name = dto.Name.Trim();
        var code = string.IsNullOrWhiteSpace(dto.Code) ? name.ToLowerInvariant() : dto.Code.Trim().ToLowerInvariant();

        if (await _db.Banks.AnyAsync(b => (b.UserId == null || b.UserId == Uid) && b.Name == name))
            return BadRequest(new { error = "Такой банк уже есть в списке" });

        var bank = new Bank
        {
            UserId = Uid,
            Name = name,
            Code = code,
            Country = string.IsNullOrWhiteSpace(dto.Country) ? "RU" : dto.Country.Trim(),
            IsSystem = false,
        };
        _db.Banks.Add(bank);
        await _db.SaveChangesAsync();
        return Ok(new BankDto(bank.Id, bank.Name, bank.Code, bank.Country, bank.IsSystem));
    }
}

/// <summary>Стартовый справочник банков — заполняется один раз.</summary>
public static class SeedBanks
{
    public static void Seed(AppDbContext db)
    {
        if (db.Banks.Any(b => b.UserId == null)) return;

        (string Name, string Code)[] defaults =
        [
            ("Т-Банк", "tbank"),
            ("Сбербанк", "sber"),
            ("ВТБ", "vtb"),
            ("Альфа-Банк", "alfabank"),
            ("Газпромбанк", "gazprombank"),
            ("Совкомбанк", "sovcombank"),
            ("Райффайзен Банк", "raiffeisen"),
            ("МТС Банк", "mtsbank"),
            ("Почта Банк", "pochtabank"),
        ];

        foreach (var (name, code) in defaults)
            db.Banks.Add(new Bank { Name = name, Code = code, Country = "RU", IsSystem = true, UserId = null });

        db.SaveChanges();
    }
}
