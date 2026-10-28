using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace SchitayKa.Api.Controllers;

public record RegisterDto(string Email, string Password, string Name);
public record LoginDto(string Email, string Password);

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public AuthController(AppDbContext db, IConfiguration cfg) { _db = db; _cfg = cfg; }

    // ---------- Пароли: PBKDF2 ----------
    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        byte[] hash = pbkdf2.GetBytes(32);
        return $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.Contains(':')) return false;
        var parts = stored.Split(':');
        byte[] salt = Convert.FromHexString(parts[0]);
        byte[] expected = Convert.FromHexString(parts[1]);
        var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(pbkdf2.GetBytes(32), expected);
    }

    public string BuildJwt(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                     new Claim("name", user.Name)],
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Укажите имя" });
        if (dto.Password.Length < 6) return BadRequest(new { error = "Пароль минимум 6 символов" });
        if (await _db.Users.AnyAsync(u => u.Email == email))
            return BadRequest(new { error = "Email уже зарегистрирован" });

        var user = new User { Email = email, Name = dto.Name.Trim(), PasswordHash = HashPassword(dto.Password) };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Стартовые категории для нового пользователя
        SeedCategories.SeedForUser(_db, user.Id);
        await _db.SaveChangesAsync();

        return Ok(new { token = BuildJwt(user), user = new { user.Id, user.Email, user.Name } });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !VerifyPassword(dto.Password, user.PasswordHash))
            return Unauthorized(new { error = "Неверный email или пароль" });
        return Ok(new { token = BuildJwt(user), user = new { user.Id, user.Email, user.Name } });
    }

    [HttpGet("me")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<IActionResult> Me()
    {
        var uid = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(uid);
        if (user is null) return Unauthorized();
        return Ok(new { user.Id, user.Email, user.Name, user.StartingBalance });
    }

    // ---------- Яндекс OAuth (authorization code flow, server-side) ----------
    // ВРЕМЕННО ОТКЛЮЧЁН: YandexOAuth:Enabled=false в appsettings.json.
    // Код сохранён — чтобы включить, поставьте "Enabled": true и заполните ClientId/ClientSecret.
    private bool YandexEnabled =>
        bool.TryParse(_cfg["YandexOAuth:Enabled"], out var en) && en;

    // GET /api/auth/yandex/start — фронт просто редиректит сюда.
    // state хранится в короткоживущем HMAC-токене, CSRF защищён.
    [HttpGet("yandex/start")]
    public IActionResult YandexStart()
    {
        if (!YandexEnabled)
            return BadRequest("Яндекс OAuth отключён (YandexOAuth:Enabled=false). Используйте вход по email и паролю.");
        var clientId = _cfg["YandexOAuth:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("ВСТАВЬ"))
            return BadRequest("Яндекс OAuth не настроен: укажите YandexOAuth:ClientId и ClientSecret в appsettings.json");
        var redirect = $"{Request.Scheme}://{Request.Host}/api/auth/yandex/callback";
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append("ya_state", state, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) });
        var url = "https://oauth.yandex.ru/authorize" +
                  $"?response_type=code&client_id={clientId}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}";
        return Redirect(url);
    }

    [HttpGet("yandex/callback")]
    public async Task<IActionResult> YandexCallback([FromQuery(Name = "code")] string? code, [FromQuery(Name = "state")] string? state)
    {
        var front = _cfg["FrontUrl"] ?? "/";
        if (!YandexEnabled)
            return Redirect($"{front}#/login?error=yandex_disabled");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Redirect($"{front}#/login?error=yandex");
        var saved = Request.Cookies["ya_state"];
        Response.Cookies.Delete("ya_state");
        if (saved != state) return Redirect($"{front}#/login?error=csrf");

        // обмен кода на токен
        using var http = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _cfg["YandexOAuth:ClientId"]!,
            ["client_secret"] = _cfg["YandexOAuth:ClientSecret"]!,
        });
        var tokResp = await http.PostAsync("https://oauth.yandex.ru/token", form);
        if (!tokResp.IsSuccessStatusCode) return Redirect($"{front}#/login?error=yandex_token");
        var tokJson = await tokResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var accessToken = tokJson.GetProperty("access_token").GetString();

        // профиль Яндекса
        var req = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
        req.Headers.Authorization = new("OAuth", accessToken);
        var profResp = await http.SendAsync(req);
        if (!profResp.IsSuccessStatusCode) return Redirect($"{front}#/login?error=yandex_profile");
        var prof = await profResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var yId = prof.GetProperty("id").GetString()!;
        var name = prof.TryGetProperty("display_name", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String
            ? dn.GetString()!
            : (prof.TryGetProperty("default_email", out var de) ? de.GetString() : "Пользователь Яндекса") ?? "Пользователь Яндекса";
        var email = prof.TryGetProperty("default_email", out var em) && em.ValueKind == System.Text.Json.JsonValueKind.String
            ? em.GetString()!.ToLowerInvariant()
            : $"yandex_{yId}@ya.local";

        var user = await _db.Users.FirstOrDefaultAsync(u => u.YandexId == yId);
        if (user is null)
        {
            // если локальный аккаунт с таким же email — привязываем Яндекс к нему
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email && u.YandexId == null);
            if (user is not null)
            {
                user.YandexId = yId;
            }
            else
            {
                // гарантируем уникальность email
                if (await _db.Users.AnyAsync(u => u.Email == email))
                    email = $"yandex_{yId}@ya.local";
                user = new User { Email = email, Name = name, YandexId = yId };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
                SeedCategories.SeedForUser(_db, user.Id);
            }
        }
        await _db.SaveChangesAsync();

        var jwt = BuildJwt(user);
        return Redirect($"{front}#/auth-success#token={Uri.EscapeDataString(jwt)}");
    }
}

// Стартовые категории личного бюджета — создаются автоматически при регистрации
public static class SeedCategories
{
    public static void SeedForUser(AppDbContext db, int userId)
    {
        (string, TxType, string)[] defaults =
        [
            ("Зарплата", TxType.Income, "#38a169"),
            ("Подработка", TxType.Income, "#319795"),
            ("Продукты", TxType.Expense, "#ed8936"),
            ("Транспорт", TxType.Expense, "#2b6cb0"),
            ("ЖКХ", TxType.Expense, "#805ad5"),
            ("Связь и интернет", TxType.Expense, "#3182ce"),
            ("Здоровье", TxType.Expense, "#e53e3e"),
            ("Развлечения", TxType.Expense, "#d53f8c"),
            ("Одежда", TxType.Expense, "#dd6b20"),
            ("Прочее", TxType.Expense, "#718096"),
        ];
        foreach (var (n, t, c) in defaults)
            db.Categories.Add(new Category { UserId = userId, Name = n, Type = t, Color = c, IsSystem = true });
    }
}
