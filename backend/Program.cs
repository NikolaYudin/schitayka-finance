using System.Text;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Ключ JWT: из конфигурации, либо сгенерированный при старте (dev)
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    jwtKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    builder.Configuration["Jwt:Key"] = jwtKey;
}
builder.Configuration["Jwt:Issuer"] ??= "SchitayKa";
builder.Configuration["Jwt:Audience"] ??= "SchitayKaClients";

// Абсолютный путь к БД: при запуске из Visual Studio рабочая папка — bin/Debug/net8.0,
// и относительный путь "data/app.db" создавал бы отдельную базу в bin (с устаревшей схемой),
// пока в backend/data лежала актуальная. Фиксируем место: <папка проекта>/data/app.db.
var dbDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dbDir);
var dbPath = Path.Combine(dbDir, "app.db");
var connStr = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrEmpty(connStr) || connStr.Contains("Data Source=data/app.db"))
    connStr = $"Data Source={dbPath}";

// --- Жёсткая самонастройка схемы: демо работает ВСЕГДА ---
// Проверка выполняется ДО регистрации DbContext и до первого подключения EF,
// чтобы исключаемые SqliteException не всплывали в отладчике Visual Studio
// («Вызвано исключение ... no such column: u.FamilyCode») и не давали 500.
// EnsureCreated() не обновляет существующую базу, поэтому любую копию app.db
// (в data/, bin/... из старых запусков VS) с устаревшей схемой пересоздаём.
var expectedCols = new[] { "SELECT FamilyCode FROM Users LIMIT 1;", "SELECT 1 FROM Accounts LIMIT 1;", "SELECT 1 FROM FamilyInvites LIMIT 1;" };

bool CopyHasCurrentSchema(string path)
{
    try
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        foreach (var sql in expectedCols)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteScalar();
        }
        return true;
    }
    catch { return false; }
}

void DeleteDbWithSidecars(string path)
{
    // Сбрасываем пулы только к УДАЛЯЕМЫМ файлам — иначе следующий запрос откроет
    // новый пустой файл до EnsureCreated() ("no such table").
    using (var poolConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolConn);
    File.Delete(path);
    foreach (var sidecar in new[] { path + "-shm", path + "-wal" })
        if (File.Exists(sidecar)) File.Delete(sidecar);
}

var dbCopies = new List<string> { dbPath };
foreach (var extra in new[]
{
    Path.Combine(AppContext.BaseDirectory, "data", "app.db"),   // bin/Debug/net8.0/data/app.db (старые запуски VS)
    Path.Combine(builder.Environment.ContentRootPath, "..", "backend", "bin", "data", "app.db"),
})
{
    var full = Path.GetFullPath(extra);
    if (!dbCopies.Contains(full, StringComparer.OrdinalIgnoreCase)) dbCopies.Add(full);
}

foreach (var copy in dbCopies.Where(File.Exists).ToList())
{
    if (!CopyHasCurrentSchema(copy))
    {
        Console.WriteLine($"[SchitayKa] Устаревшая схема БД обнаружена — пересоздаю {copy} ...");
        DeleteDbWithSidecars(copy);
    }
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connStr));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = "name",
        };
    });

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// создание БД (для прода можно заменить на EF Migrations)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // --- Самовосстановление: демо работает ВСЕГДА ---
    // Копии БД с устаревшей схемой уже пересозданы ДО регистрации DbContext
    // (см. начало файла) — здесь только создаём базу при её отсутствии.
    db.Database.EnsureCreated();

    if (db.Users.Any(u => u.Email == "demo@schitayka.ru"))
    {
        Console.WriteLine("[SchitayKa] Демо-аккаунт готов: demo@schitayka.ru / demo123");
    }
    else
    {
        if (db.Users.Any())
        {
            Console.WriteLine("[SchitayKa] Демо-аккаунт отсутствует — пересоздаю базу с чистым сидом ...");
            await db.Database.EnsureDeletedAsync();
            db.Database.EnsureCreated();
        }

            string Hash(string pw) => SchitayKa.Api.Controllers.AuthController.HashPassword(pw);
            var demo = new User { Email = "demo@schitayka.ru", Name = "Демо-пользователь", PasswordHash = Hash("demo123"), StartingBalance = 45000 };
            db.Users.Add(demo);
            await db.SaveChangesAsync(); // фиксируем Id пользователя
    
            SchitayKa.Api.Controllers.SeedCategories.SeedForUser(db, demo.Id);
            await db.SaveChangesAsync(); // категории должны попасть в БД до поиска по имени
            var cats = db.Categories.Where(c => c.UserId == demo.Id).ToList();
            var cZarplata = cats.First(c => c.Name == "Зарплата");
            var cProducts = cats.First(c => c.Name == "Продукты");
            var cAuto = cats.First(c => c.Name == "Транспорт");
            var cZhkkh = cats.First(c => c.Name == "ЖКХ");
            var cFun = cats.First(c => c.Name == "Развлечения");
    
            var today = DateOnly.FromDateTime(DateTime.Today);
            var m = new DateOnly(today.Year, today.Month, 1);
            var prev = m.AddMonths(-1);
    
            db.Transactions.AddRange(new[]
            {
                new Transaction { Category = cZarplata, UserId = demo.Id, Type = TxType.Income, Amount = 95000, Date = prev, Comment = "Зарплата" },
                new Transaction { Category = cProducts, UserId = demo.Id, Type = TxType.Expense, Amount = 21350, Date = prev, Comment = "Продукты за месяц" },
                new Transaction { Category = cAuto, UserId = demo.Id, Type = TxType.Expense, Amount = 14700, Date = prev.AddDays(2), Comment = "Автокредит + бензин" },
                new Transaction { Category = cZhkkh, UserId = demo.Id, Type = TxType.Expense, Amount = 8200, Date = prev, Comment = "ЖКХ" },
                new Transaction { Category = cZarplata, UserId = demo.Id, Type = TxType.Income, Amount = 95000, Date = m, Comment = "Зарплата" },
                new Transaction { Category = cProducts, UserId = demo.Id, Type = TxType.Expense, Amount = 12350, Date = m, Comment = "Продукты" },
                new Transaction { Category = cAuto, UserId = demo.Id, Type = TxType.Expense, Amount = 9400, Date = m, Comment = "Бензин и парковка" },
                new Transaction { Category = cZhkkh, UserId = demo.Id, Type = TxType.Expense, Amount = 8200, Date = m, Comment = "ЖКХ" },
                new Transaction { Category = cFun, UserId = demo.Id, Type = TxType.Expense, Amount = 6500, Date = m.AddDays(3), Comment = "Концерт и кафе" },
            });
    
            // Лимиты на текущий месяц
            db.BudgetLimits.AddRange(
                new BudgetLimit { UserId = demo.Id, CategoryId = cProducts.Id, Year = m.Year, Month = m.Month, Limit = 20000 },
                new BudgetLimit { UserId = demo.Id, CategoryId = cAuto.Id, Year = m.Year, Month = m.Month, Limit = 8000 },
                new BudgetLimit { UserId = demo.Id, CategoryId = cZhkkh.Id, Year = m.Year, Month = m.Month, Limit = 12000 },
                new BudgetLimit { UserId = demo.Id, CategoryId = cFun.Id, Year = m.Year, Month = m.Month, Limit = 5000 });
    
            db.SavingsGoals.AddRange(
                new SavingsGoal { UserId = demo.Id, Name = "Подушка безопасности", TargetAmount = 300000, CurrentAmount = 120000, Deadline = new DateOnly(today.Year, 12, 31) },
                new SavingsGoal { UserId = demo.Id, Name = "Отпуск летом", TargetAmount = 150000, CurrentAmount = 4468.94m, Deadline = today.AddMonths(5) });
    
            // Счета: дебетовая карта с кэшбэком, кредитка (долг), наличные, вклад
            var aDebit = new Account { UserId = demo.Id, Name = "Основная карта", Type = AccountType.DebitCard, StartingBalance = 30000, CashbackRate = 5, Color = "#2b6cb0" };
            var aCredit = new Account { UserId = demo.Id, Name = "Кредитная «Кэшбэлка»", Type = AccountType.CreditCard, StartingBalance = 25000, CreditLimit = 120000, CashbackRate = 10, Color = "#e53e3e" };
            var aCash = new Account { UserId = demo.Id, Name = "Наличные", Type = AccountType.Cash, StartingBalance = 8000, Color = "#38a169" };
            var aDeposit = new Account { UserId = demo.Id, Name = "Вклад «Полугодие»", Type = AccountType.Deposit, StartingBalance = 100000, Color = "#805ad5" };
            db.Accounts.AddRange(aDebit, aCredit, aCash, aDeposit);
            await db.SaveChangesAsync(); // фиксируем Id счетов перед привязкой операций
    
            // Привяём часть демо-операций к счетам (покупки по кредитке — увеличивают долг)
            var demoTxs = db.Transactions.Where(t => t.UserId == demo.Id).ToList();
            foreach (var t in demoTxs)
            {
                if (t.Type == TxType.Income && t.CategoryId == cZarplata.Id) t.AccountId = aDebit.Id;      // зарплата на дебетовую
                else if (t.CategoryId == cFun.Id) t.AccountId = aCredit.Id;                                 // развлечения — в кредит
                else if (t.CategoryId == cAuto.Id) t.AccountId = aCash.Id;                                  // транспорт — наличными
                else if (t.Type == TxType.Expense) t.AccountId = aDebit.Id;                                  // остальное — основная карта
            }
            // Погашение кредитки: перевод с дебетовой на кредитную (уменьшает долг)
            db.Transactions.Add(new Transaction { Category = cZarplata, UserId = demo.Id, Type = TxType.Income, Amount = 15000, Date = m.AddDays(5), Comment = "Погашение кредитной карты", AccountId = aCredit.Id });
    
            await db.SaveChangesAsync();
            
        Console.WriteLine("[seed] Демо-данные созданы: demo@schitayka.ru / demo123");
    }
}

app.UseSwagger();
app.UseSwaggerUI();

// dev-режим: если собранного wwwroot/index.html нет — проксируем на Vite (5173),
// чтобы фронтенд можно было запускать отдельно через npm run dev.
if (app.Environment.IsDevelopment() && FindIndexHtml(app.Environment) is null)
{
    app.Use(async (ctx, next) =>
    {
        if (HttpMethods.IsGet(ctx.Request.Method) && !ctx.Request.Path.StartsWithSegments("/api"))
        {
            try
            {
                var target = new UriBuilder("http://localhost:5173") { Path = ctx.Request.Path.Value ?? "/" };
                using var http = new HttpClient();
                var req = new HttpRequestMessage(HttpMethod.Get, target.Uri);
                var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                foreach (var h in resp.Headers)
                    ctx.Response.Headers[h.Key] = h.Value.ToArray();
                foreach (var h in resp.Content.Headers)
                    ctx.Response.Headers[h.Key] = h.Value.ToArray();
                ctx.Response.Headers.Remove("Transfer-Encoding");
                await resp.Content.CopyToAsync(ctx.Response.Body);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsync("Dev-фронтенд не запущен. Либо включите 'npm run dev' в папке frontend/, либо соберите продакшен-версию (npm run build) и перезапустите сервер.");
            }
            return;
        }
        await next();
    });
}

// SPA из wwwroot (прод-сборка: npm run build -> backend/wwwroot)
// ContentRoot при запуске из Visual Studio может указывать на репозиторий, а не на bin/... —
// поэтому ищем index.html в нескольких типовых местах.
string? FindIndexHtml(IWebHostEnvironment env)
{
    var candidates = new List<string>
    {
        Path.Combine(env.ContentRootPath, "wwwroot", "index.html"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"),
        Path.Combine(env.ContentRootPath, "..", "backend", "wwwroot", "index.html"),
        Path.Combine(env.ContentRootPath, "..", "SchitayKa.Api", "wwwroot", "index.html"),
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (File.Exists(full)) return full;
    }
    return null;
}

string? FindWebRoot(IWebHostEnvironment env)
{
    var idx = FindIndexHtml(env);
    return idx is null ? null : Path.GetDirectoryName(idx);
}

static string GetContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
{
    ".html" => "text/html; charset=utf-8",
    ".js" => "text/javascript",
    ".mjs" => "text/javascript",
    ".css" => "text/css",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".jpg" => "image/jpeg",
    ".ico" => "image/x-icon",
    ".json" => "application/json",
    ".woff2" => "font/woff2",
    _ => "application/octet-stream",
};

// Статические файлы из wwwroot. При запуске из Visual Studio ContentRoot/web root
// могут указывать не на папку проекта — ищем собранный index.html и отдаём файлы вручную.
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var webRoot = FindWebRoot(env);
if (webRoot is not null)
{
    Console.WriteLine($"[SchitayKa] Frontend found at: {webRoot}");
    try { env.WebRootPath = webRoot; } catch { /* если уже инициализирован — не страшно */ }
}
else
{
    Console.WriteLine("[SchitayKa] WARNING: frontend build (wwwroot/index.html) not found.");
    Console.WriteLine("[SchitayKa] Run setup.cmd once (installs npm deps), then F5 auto-builds everything.");
}

app.Use(async (ctx, next) =>
{
    if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
        && !ctx.Request.Path.StartsWithSegments("/api") && webRoot is not null)
    {
        var relRaw = ctx.Request.Path.ToString().TrimStart('/');
        if (relRaw.Length == 0) relRaw = "index.html";
        var rel = relRaw.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(webRoot, rel));
        // защита от выхода за пределы wwwroot + только реальные файлы
        if (candidate.StartsWith(Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && File.Exists(candidate))
        {
            ctx.Response.ContentType = GetContentType(candidate);
            await ctx.Response.SendFileAsync(candidate);
            return;
        }
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", app = "СчитайКа" }));

// fallback для SPA-роутера (не трогаем /api)
app.MapFallback("/{*path}", async ctx =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        var index = FindIndexHtml(app.Environment);
        if (index is not null)
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.SendFileAsync(index);
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>СчитайКа — соберите фронтенд</title>" +
                "<style>body{font-family:'Segoe UI',sans-serif;background:#f7fafc;color:#2d3748;display:flex;align-items:center;justify-content:center;min-height:90vh;margin:0}" +
                ".card{background:#fff;border-radius:12px;padding:32px;max-width:640px;box-shadow:0 2px 10px rgba(0,0,0,.08)}h1{color:#1a365d;font-size:1.4em}code{background:#edf2f7;padding:2px 6px;border-radius:4px}</style></head>" +
                "<body><div class=\"card\"><h1>&#128274 Продакшен-сборка фронтенда отсутствует</h1>" +
                "<p>Бэкенд запущен, но файл <code>backend/wwwroot/index.html</code> не найден.</p>" +
                "<p><b>Что сделать:</b> закройте проект в Visual Studio, дважды запустите <code>build-frontend.cmd</code> из корня репозитория, дождитесь SUCCESS, затем снова F5.</p>" +
                "</div></body></html>");
        }
    }
    else
    {
        ctx.Response.StatusCode = 404;
    }
});

app.Run();
