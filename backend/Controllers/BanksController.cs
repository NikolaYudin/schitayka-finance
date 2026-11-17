using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Справочник банков (Задача 6 + 0.3/N5): системный список основных банков РФ
/// с брендовыми цветами и собственные банки пользователя.
/// </summary>
[ApiController]
[Route("api/banks")]
[Authorize]
public class BanksController : ControllerBase
{
    private readonly AppDbContext _db;
    public BanksController(AppDbContext db) => _db = db;
    private int Uid => UserAccess.UidOf(User);

    public record BankDto(int Id, string Name, string Code, string Country, bool IsSystem,
        string? ShortName = null, string? BrandColor = null, string? LogoUrl = null);

    /// <summary>Системный справочник + собственные банки пользователя.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });

        var query = _db.Banks
            .Where(b => b.UserId == null || b.UserId == Uid);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            query = query.Where(b => b.Name.ToLower().Contains(s)
                || (b.ShortName != null && b.ShortName.ToLower().Contains(s)));
        }

        var list = await query
            .OrderBy(b => b.IsSystem ? 0 : 1).ThenBy(b => b.Name)
            .Select(b => new BankDto(b.Id, b.Name, b.Code, b.Country, b.IsSystem,
                b.ShortName, b.BrandColor, b.LogoUrl))
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
            ShortName = string.IsNullOrWhiteSpace(dto.ShortName) ? null : dto.ShortName.Trim(),
            BrandColor = NormalizeColor(dto.BrandColor),
            LogoUrl = string.IsNullOrWhiteSpace(dto.LogoUrl) ? null : dto.LogoUrl.Trim(),
        };
        _db.Banks.Add(bank);
        await _db.SaveChangesAsync();
        return Ok(new BankDto(bank.Id, bank.Name, bank.Code, bank.Country, bank.IsSystem,
            bank.ShortName, bank.BrandColor, bank.LogoUrl));
    }

    /// <summary>Изменить свой банк (системные банки неизменяемы).</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] BankDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == id);
        if (bank is null || (bank.UserId != null && bank.UserId != Uid))
            return NotFound(new { error = "Банк не найден" });
        if (bank.IsSystem) return BadRequest(new { error = "Системный банк изменить нельзя" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название банка обязательно" });

        bank.Name = dto.Name.Trim();
        if (!string.IsNullOrWhiteSpace(dto.Code))
            bank.Code = dto.Code.Trim().ToLowerInvariant();
        bank.ShortName = string.IsNullOrWhiteSpace(dto.ShortName) ? null : dto.ShortName.Trim();
        bank.BrandColor = NormalizeColor(dto.BrandColor);
        bank.LogoUrl = string.IsNullOrWhiteSpace(dto.LogoUrl) ? null : dto.LogoUrl.Trim();
        await _db.SaveChangesAsync();
        return Ok(new BankDto(bank.Id, bank.Name, bank.Code, bank.Country, bank.IsSystem,
            bank.ShortName, bank.BrandColor, bank.LogoUrl));
    }

    /// <summary>Удалить свой банк, если на него не ссылаются счета и портфели.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == id);
        if (bank is null || (bank.UserId != null && bank.UserId != Uid))
            return NotFound(new { error = "Банк не найден" });
        if (bank.IsSystem) return BadRequest(new { error = "Системный банк удалить нельзя" });

        var referenced = await _db.Accounts.AnyAsync(a => a.BankId == id)
            || await _db.InvestmentPortfolios.AnyAsync(p => p.BankId == id);
        if (referenced)
            return BadRequest(new { error = "К этому банку привязаны счета или портфели — сначала выберите другой банк" });

        _db.Banks.Remove(bank);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Проверяем формат цвета: #RGB или #RRGGBB, иначе null.</summary>
    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim();
        return c.StartsWith('#') && (c.Length == 4 || c.Length == 7) ? c : null;
    }
}

/// <summary>
/// Стартовый справочник банков. Сид идемпотентный (0.3/N5): проверяем по коду каждый банк
/// и добавляем отсутствующие, не трогая существующие строки; пустые брендовые поля
/// у уже сидированных банков дозаполняются. Логотип не задаём — клиент рисует монограмму,
/// чтобы приложение работало офлайн и не ходило за картинками наружу (см. бэклог B17).
/// </summary>
public static class SeedBanks
{
    public record Row(string Name, string Code, string ShortName, string BrandColor);

    public static readonly Row[] Defaults =
    [
        new("Т-Банк", "tbank", "Т", "#FFDD2D"),
        new("Сбербанк", "sber", "Сбер", "#21A038"),
        new("ВТБ", "vtb", "ВТБ", "#0A2896"),
        new("Альфа-Банк", "alfabank", "Альфа", "#EF3124"),
        new("Газпромбанк", "gazprombank", "ГПБ", "#0079C2"),
        new("Совкомбанк", "sovcombank", "Совком", "#0072BC"),
        new("Райффайзенбанк", "raiffeisen", "Райф", "#FFED00"),
        new("МТС Банк", "mtsbank", "МТС", "#E30611"),
        new("Почта Банк", "pochtabank", "Почта", "#009FDF"),
        new("Банк Открытие", "openbank", "Открытие", "#005BFF"),
        new("ПСБ", "psb", "ПСБ", "#102A83"),
        new("Россельхозбанк", "rshb", "РСХБ", "#00539B"),
        new("Московский кредитный банк", "mkb", "МКБ", "#16366F"),
        new("Уралсиб", "uralsib", "Уралсиб", "#005CA9"),
        new("Ренессанс банк", "rencredit", "Ренессанс", "#009B77"),
        new("Абсолют Банк", "absolutbank", "Абсолют", "#DA291C"),
        new("СПБ Банк", "spbbank", "СПБ", "#00437A"),
        new("Экспобанк", "expobank", "Экспо", "#0F7B7B"),
        new("РНКБ Банк", "rnkb", "РНКБ", "#00549E"),
        new("БКС Мир", "bcs", "БКС", "#0A57A5"),
        new("Финам", "finam", "Финам", "#E4302B"),
        new("Хоум Банк", "homecredit", "Хоум", "#FF6600"),
        new("Банк Зенит", "zenit", "Зенит", "#005B94"),
        new("Тинькофф Банк", "tinkoff", "Тинькофф", "#FFDD2D"),   // архивное название Т-Банка
        new("Банк Авангард", "avangard", "Авангард", "#003876"),
    ];

    public static void Seed(AppDbContext db)
    {
        var existing = db.Banks.Where(b => b.UserId == null).ToList();

        foreach (var (name, code, shortName, brandColor) in Defaults)
        {
            var row = existing.FirstOrDefault(b => b.Code == code);
            if (row is null)
            {
                db.Banks.Add(new Bank
                {
                    Name = name, Code = code, Country = "RU", IsSystem = true,
                    ShortName = shortName, BrandColor = brandColor,
                });
            }
            else
            {
                // Дозаполняем брендовые поля у банков, сидированных до N5.
                row.ShortName ??= shortName;
                row.BrandColor ??= brandColor;
            }
        }

        db.SaveChanges();
    }
}
