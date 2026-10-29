using System.Text;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// --- Автосборка фронтенда при старте ---
// backend/wwwroot пересобирается сам, если его нет или исходники frontend/ новее бандла.
// Выполняется ДО создания приложения, чтобы сервер всегда отдавал актуальный index.html.
// Отключается переменной окружения SCHITAYKA_AUTOBUILD_FRONTEND=false (или 0).
static void EnsureFrontendBuilt()
{
    try
    {
        var flag = Environment.GetEnvironmentVariable("SCHITAYKA_AUTOBUILD_FRONTEND");
        if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase) || flag == "0") return;

        // frontend/ ищём рядом с папкой проекта/бинарников (VS запускает из bin/Debug/net8.0)
        string[] bases = { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        string? frontendDir = null, wwwrootDir = null;
        foreach (var b in bases)
        {
            var dir = new DirectoryInfo(b);
            for (int depth = 0; dir != null && depth < 6; depth++, dir = dir.Parent)
            {
                var fe = Path.Combine(dir.FullName, "frontend", "package.json");
                var wr = Path.Combine(dir.FullName, "backend", "wwwroot", "index.html");
                if (File.Exists(fe)) { frontendDir = Path.Combine(dir.FullName, "frontend"); break; }
            }
            if (frontendDir is not null)
            {
                var root = Directory.GetParent(frontendDir)!.FullName;
                wwwrootDir = Path.Combine(root, "backend", "wwwroot");
                break;
            }
        }
        if (frontendDir is null) return; // структура проекта не найдена — молча пропускаем

        // Нужна ли сборка: 1) нет бандла; 2) хеш источников не совпадает с записанным
        // (mtime после git pull ненадёжен — файлы могут быть «старее» index.html);
        // 3) любой исходник свежее index.html.
        var indexPath = Path.Combine(wwwrootDir!, "index.html");
        var srcDir = Path.Combine(frontendDir, "src");
        var configFiles = new[]
        {
            Path.Combine(frontendDir, "index.html"),
            Path.Combine(frontendDir, "package.json"),
            Path.Combine(frontendDir, "vite.config.ts"),
            Path.Combine(frontendDir, "tsconfig.json"),
        };
        string CurrentHash()
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var ms = new MemoryStream();
            void Add(string path)
            {
                if (File.Exists(path))
                {
                    ms.Write(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path) + "|"));
                    ms.Write(File.ReadAllBytes(path));
                }
            }
            if (Directory.Exists(srcDir))
                foreach (var f in Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                    Add(f);
            foreach (var f in configFiles) Add(f);
            return Convert.ToHexString(sha.ComputeHash(ms.ToArray()));
        }
        var hashFile = Path.Combine(wwwrootDir!, ".frontend-hash");
        var currentHash = CurrentHash();
        var storedHash = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : null;
        bool needsBuild = !File.Exists(indexPath) || storedHash != currentHash;
        if (!needsBuild)
        {
            var lastWrite = File.GetLastWriteTimeUtc(indexPath);
            foreach (var f in Directory.Exists(srcDir)
                         ? Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories).Concat(configFiles)
                         : configFiles)
            {
                try { if (File.GetLastWriteTimeUtc(f) > lastWrite) { needsBuild = true; break; } } catch { }
            }
        }
        if (!needsBuild) return;

        // npm должен быть в PATH
        static bool Have(string exe)
        {
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            var name = OperatingSystem.IsWindows() ? exe + ".cmd" : exe;
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep))
            {
                if (p.Trim().Length == 0) continue;
                try { if (File.Exists(Path.Combine(p.Trim(), name))) return true; } catch { }
            }
            return false;
        }
        if (!Have("npm"))
        {
            Console.WriteLine("[SchitayKa] npm не найден в PATH — автосборка фронтенда пропущена.");
            return;
        }

        var nodeModules = Path.Combine(frontendDir, "node_modules");
        var pkgLock = Path.Combine(frontendDir, "package-lock.json");
        bool depsOk = Directory.Exists(nodeModules)
            && (!File.Exists(pkgLock)
                || Directory.GetFileSystemEntries(nodeModules).Length > 0
                   && File.GetLastWriteTimeUtc(pkgLock) <= File.GetLastWriteTimeUtc(Path.Combine(nodeModules, ".package-lock.json")));
        // Если package-lock новее установленного .package-lock.json — досстанавливаем зависимости
        var innerLock = Path.Combine(nodeModules, ".package-lock.json");
        if (depsOk && File.Exists(pkgLock) && File.Exists(innerLock)
            && File.GetLastWriteTimeUtc(pkgLock) > File.GetLastWriteTimeUtc(innerLock))
            depsOk = false;

        Console.WriteLine($"[SchitayKa] Frontend устарел — собираю автоматически ({frontendDir})...");
        static int RunNpm(string args, string workDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows()) { psi.Arguments = "/c npm " + args; }
            else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("npm " + System.Text.RegularExpressions.Regex.Replace(args, "\"([^\" ]+)\"", "'$1'")); }
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(300_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine("[SchitayKa] npm " + args + ": превышен таймаут 5 мин.");
                return -1;
            }
            var outp = stdoutTask.GetAwaiter().GetResult();
            var errp = stderrTask.GetAwaiter().GetResult();
            var tail = string.Join(Environment.NewLine,
                (outp + Environment.NewLine + errp).Split('\n')
                    .Where(l => l.Trim().Length > 0).TakeLast(8));
            Console.WriteLine(tail);
            return proc.ExitCode;
        }

        if (!depsOk)
        {
            Console.WriteLine("[SchitayKa] Устанавливаю зависимости (npm install)...");
            if (RunNpm("install", frontendDir) != 0)
            {
                Console.WriteLine("[SchitayKa] WARNING: npm install завершился с ошибкой — использую текущий wwwroot.");
                return;
            }
        }
        if (RunNpm("run build", frontendDir) != 0)
            Console.WriteLine("[SchitayKa] WARNING: npm run build завершился с ошибкой — использую текущий wwwroot.");
        else
        {
            try { File.WriteAllText(hashFile, CurrentHash()); } catch { }
            Console.WriteLine("[SchitayKa] Фронтенд собран в backend/wwwroot.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SchitayKa] Автосборка фронтенда пропущена: {ex.Message}");
    }
}
EnsureFrontendBuilt();

// wwwroot мог отсутствовать до автосборки выше — статические веб-ассеты ASP.NET Core
// падают с DirectoryNotFoundException, если папки нет на момент CreateBuilder.
// Гарантируем её существование (в dev-режиме содержимое отдаётся ручным обработчиком).
try
{
    var wrProbe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot");
    if (!Directory.Exists(wrProbe))
    {
        // ищем папку проекта так же, как в EnsureFrontendBuilt
        foreach (var b in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(b);
            for (int i = 0; d != null && i < 6; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "backend", "wwwroot")) || File.Exists(Path.Combine(d.FullName, "backend", "SchitayKa.Api.csproj")))
                { Directory.CreateDirectory(Path.Combine(d.FullName, "backend", "wwwroot")); break; }
        }
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));
    }
}
catch { }

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
// Таблицы/колонки актуальной схемы (имена как в EF: DbSet FamilyInvitations).
string? SqliteError(string path, string sql)
{
    try
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteScalar();
        return null;
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) { return ex.SqliteErrorCode == 1 ? ex.Message : null; }
}

// Полный список колонок актуальной модели EF (таблица -> колонки). Используется для
// точечного ALTER TABLE ADD COLUMN — данные пользователя при этом сохраняются.
var AddCols = new (string Table, string Col, string Def)[]
{
    ("Users", "FamilyCode", "TEXT NULL"),
    ("Transactions", "AccountId", "INTEGER NULL"),
};

// Колонки таблиц, которые могут полностью отсутствовать в старой базе.
var NewTableCols = new (string Table, (string Col, string Def)[] Cols)[]
{
    ("Accounts", new (string, string)[]
    {
        ("Id", "INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT"),
        ("UserId", "INTEGER NOT NULL"),
        ("Name", "TEXT NOT NULL"),
        ("Type", "INTEGER NOT NULL"),
        ("StartingBalance", "TEXT NOT NULL"),
        ("CreditLimit", "TEXT NOT NULL DEFAULT '0'"),
        ("CashbackRate", "TEXT NOT NULL DEFAULT '0'"),
        ("CashbackAccrued", "TEXT NOT NULL DEFAULT '0'"),
        ("Color", "TEXT NULL"),
        ("IsArchived", "INTEGER NOT NULL DEFAULT 0"),
        ("CreatedAt", "TEXT NOT NULL"),
    }),
    ("FamilyInvitations", new (string, string)[]
    {
        ("Id", "INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT"),
        ("OwnerUserId", "INTEGER NOT NULL"),
        ("Code", "TEXT NOT NULL"),
        ("Email", "TEXT NULL"),
        ("CreatedAt", "TEXT NOT NULL"),
        ("ExpiresAt", "TEXT NOT NULL"),
        ("AcceptedAt", "TEXT NULL"),
        ("AcceptedByUserId", "INTEGER NULL"),
    }),
};

bool IsStaleCopy(string path)
{
    foreach (var (t, c, _) in AddCols)
        if (SqliteError(path, $"SELECT \"{c}\" FROM \"{t}\" LIMIT 1;") is not null) return true;
    foreach (var nt in NewTableCols)
        if (SqliteError(path, $"SELECT 1 FROM \"{nt.Table}\" LIMIT 1;") is not null) return true;
    return false;
}

// Удаляет «мусорные» копии БД (полностью устаревшая схема, без ценных данных):
// сначала сбрасываем пулы, затем жёсткий retry-delete (файл мог быть занят процессом VS/антивирусом).
void DeleteStaleDbCopy(string path)
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in new[] { path, path + "-shm", path + "-wal" })
    {
        try
        {
            if (!File.Exists(f)) continue;
            for (int attempt = 0; ; attempt++)
            {
                try { File.Delete(f); break; }
                catch when (attempt < 8) { Thread.Sleep(150 * (attempt + 1)); }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[SchitayKa] Не удалось удалить {f}: {ex.Message}"); }
    }
}

// Гармонизация схемы ВСЕХ известных копий app.db (data/ и bin/... из старых запусков VS):
//  - копия со СТАРОЙ схемой и БЕЗ пользовательских данных -> удаляется (будет пересоздана);
//  - копия со СТАРОЙ схемой, но с данными -> чинится на месте (CREATE TABLE + ALTER TABLE),
//    данные сохраняются;
//  - актуальная копия остаётся как есть.
void HarmonizeDbCopies(IEnumerable<string> copies)
{
    foreach (var path in copies.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
    {
        if (!IsStaleCopy(path)) continue;

        long userCount = 0;
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            conn.Open();
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM Users;";
            userCount = Convert.ToInt64(q.ExecuteScalar());
        }
        catch { /* таблицы Users нет или файл битый — считаем копию пустой */ }

        if (userCount == 0)
        {
            Console.WriteLine($"[SchitayKa] Устаревшая пустая копия БД — удаляю {path} (будет пересоздана с актуальной схемой)");
            DeleteStaleDbCopy(path);
            continue;
        }

        // Есть пользовательские данные — восстанавливаем схему на месте, ничего не удаляя.
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            conn.Open();
            void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

            // 1) Полностью отсутствующие таблицы создаём целиком (IF NOT EXISTS — безопасно).
            foreach (var (table, cols) in NewTableCols)
            {
                var colDefs = string.Join(",\n        ", cols.Select(cc => $"\"{cc.Col}\" {cc.Def}"));
                Exec($@"CREATE TABLE IF NOT EXISTS ""{table}"" (
        {colDefs});");
                Console.WriteLine($"[SchitayKa] Схема обновлена на месте: создана таблица {table} (данные сохранены)");
            }

            // 2) Недостающие колонки — точечно через ALTER TABLE (SQLite поддерживает без пересоздания таблицы).
            foreach (var (table, col, def) in AddCols)
            {
                if (SqliteError(path, $"SELECT 1 FROM \"{table}\" LIMIT 1;") is not null) continue; // таблицы ещё нет
                if (SqliteError(path, $"SELECT \"{col}\" FROM \"{table}\" LIMIT 1;") is null) continue; // колонка уже есть
                Exec($"ALTER TABLE \"{table}\" ADD COLUMN \"{col}\" {def};");
                Console.WriteLine($"[SchitayKa] Схема обновлена на месте: добавлена колонка {table}.{col} (данные сохранены)");
            }

            // 3) Индексы, ожидаемые моделью EF.
            Exec(@"CREATE INDEX IF NOT EXISTS ""IX_Accounts_UserId"" ON ""Accounts"" (""UserId"");");
            Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_FamilyInvitations_Code"" ON ""FamilyInvitations"" (""Code"");");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SchitayKa] Не удалось починить схему {path}: {ex.Message}");
        }

        // Финальная проверка: если после всех починок что-то всё ещё не читается —
        // это повреждённый файл; оставляем его в поведении «пересоздать», но только
        // если он НЕ является основным (основной чиним до победного выше).
        if (IsStaleCopy(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[SchitayKa] Копия {path} осталась несовместимой — удаляю её");
            DeleteStaleDbCopy(path);
        }
    }
}

HarmonizeDbCopies(new[]
{
    dbPath,
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "app.db")),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "bin", "Debug", "net8.0", "data", "app.db")),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "backend", "bin", "Debug", "net8.0", "data", "app.db")),
});

// Если основной базы нет — создаём её сразу (отдельным контекстом), чтобы ни один
// EF-запрос никогда не обратился к несуществующей/устаревшей схеме.
if (!File.Exists(dbPath))
{
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options;
    using var initCtx = new AppDbContext(opts);
    initCtx.Database.EnsureCreated();
}
else
{
    // База есть: убедимся, что демо-аккаунт существует (мог быть потерян при удалении bin-копии).
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options;
    using var checkCtx = new AppDbContext(opts);
    if (!checkCtx.Users.Any(u => u.Email == "demo@schitayka.ru"))
    {
        Console.WriteLine("[SchitayKa] В основной базе нет демо-аккаунта — пересоздаю её с чистым сидом ...");
        checkCtx.Database.EnsureDeleted();
        checkCtx.Database.EnsureCreated();
    }
}

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    // 1) Если базы нет — EnsureCreated создаёт её сразу с актуальной схемой.
    db.Database.EnsureCreated();
    // 2) На всякий случай ещё раз гармонизируем все известные копии app.db
    //    (могли появиться, пока приложение было выключено). Данные сохраняются.
    HarmonizeDbCopies(new[]
    {
        dbPath,
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "app.db")),
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "bin", "Debug", "net8.0", "data", "app.db")),
    });
    // 3) Схема могла измениться извне — сбрасываем пулы соединений EF, чтобы следующие
    //    запросы использовали свежие sqlite-хэндлы.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    var demoUser = db.Users.FirstOrDefault(u => u.Email == "demo@schitayka.ru");
    if (demoUser is not null)
    {
        // Демо есть — при необходимости дозаполняем демо-контент (счета/семья/переводы),
        // если база была создана более старым сидом.
        if (!db.Accounts.Any(a => a.UserId == demoUser.Id) || !db.FamilyInvitations.Any(i => i.OwnerUserId == demoUser.Id))
        {
            Console.WriteLine("[SchitayKa] Демо-аккаунт найден, но демо-контент неполный — дозаполняю ...");
            await SeedDemoDataAsync(db, demoUser);
        }
        Console.WriteLine("[SchitayKa] Демо-аккаунт готов: demo@schitayka.ru / demo123 (семья: spouse@schitayka.ru / spouse123)");
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

        await SeedDemoDataAsync(db, demo);
        Console.WriteLine("[seed] Демо-данные созданы: demo@schitayka.ru / demo123 (семья: spouse@schitayka.ru / spouse123)");
    }
}

// --- Единый сид демо-данных (используется и при первичном создании БД,
//     и при авто-восстановлении) ---
async Task SeedDemoDataAsync(AppDbContext db, User demo)
{
    // Категории: сидим только если их ещё нет (иначе SeedForUser создаст дубликаты)
    if (!db.Categories.Any(c => c.UserId == demo.Id))
        SchitayKa.Api.Controllers.SeedCategories.SeedForUser(db, demo.Id);
    await db.SaveChangesAsync(); // категории должны попасть в БД до поиска по имени
    var cats = db.Categories.Where(c => c.UserId == demo.Id).ToList();
    var alreadySeeded = db.Transactions.Any(t => t.UserId == demo.Id); // дозаполнение без дублей операций
    var cZarplata = cats.First(c => c.Name == "Зарплата");
    var cProducts = cats.First(c => c.Name == "Продукты");
    var cAuto = cats.First(c => c.Name == "Транспорт");
    var cZhkkh = cats.First(c => c.Name == "ЖКХ");
    var cFun = cats.First(c => c.Name == "Развлечения");
    var cOther = cats.First(c => c.Name == "Прочее");

    var today = DateOnly.FromDateTime(DateTime.Today);
    var m = new DateOnly(today.Year, today.Month, 1);
    var prev = m.AddMonths(-1);

    if (!alreadySeeded)
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
    if (!db.BudgetLimits.Any(l => l.UserId == demo.Id))
    db.BudgetLimits.AddRange(
        new BudgetLimit { UserId = demo.Id, CategoryId = cProducts.Id, Year = m.Year, Month = m.Month, Limit = 20000 },
        new BudgetLimit { UserId = demo.Id, CategoryId = cAuto.Id, Year = m.Year, Month = m.Month, Limit = 8000 },
        new BudgetLimit { UserId = demo.Id, CategoryId = cZhkkh.Id, Year = m.Year, Month = m.Month, Limit = 12000 },
        new BudgetLimit { UserId = demo.Id, CategoryId = cFun.Id, Year = m.Year, Month = m.Month, Limit = 5000 });

    if (!db.SavingsGoals.Any(g => g.UserId == demo.Id))
    db.SavingsGoals.AddRange(
        new SavingsGoal { UserId = demo.Id, Name = "Подушка безопасности", TargetAmount = 300000, CurrentAmount = 120000, Deadline = new DateOnly(today.Year, 12, 31) },
        new SavingsGoal { UserId = demo.Id, Name = "Отпуск летом", TargetAmount = 150000, CurrentAmount = 4468.94m, Deadline = today.AddMonths(5) });

    // Счета: дебетовая карта с кэшбэком, кредитка (долг), наличные, вклад
    Account aDebit = new Account { UserId = demo.Id, Name = "Основная карта", Type = AccountType.DebitCard, StartingBalance = 30000, CashbackRate = 5, Color = "#2b6cb0" };
    Account aCredit = new Account { UserId = demo.Id, Name = "Кредитная «Кэшбэлка»", Type = AccountType.CreditCard, StartingBalance = 25000, CreditLimit = 120000, CashbackRate = 10, Color = "#e53e3e" };
    Account aCash = new Account { UserId = demo.Id, Name = "Наличные", Type = AccountType.Cash, StartingBalance = 8000, Color = "#38a169" };
    Account aDeposit = new Account { UserId = demo.Id, Name = "Вклад «Полугодие»", Type = AccountType.Deposit, StartingBalance = 100000, Color = "#805ad5" };
    var existingAccounts = db.Accounts.Where(a => a.UserId == demo.Id).ToList();
    Account? ByName(string n) => existingAccounts.FirstOrDefault(a => a.Name == n);
    aDebit = ByName("Основная карта") ?? aDebit;
    aCredit = ByName("Кредитная «Кэшбэлка»") ?? aCredit;
    aCash = ByName("Наличные") ?? aCash;
    aDeposit = ByName("Вклад «Полугодие»") ?? aDeposit;
    var toAdd = new[] { aDebit, aCredit, aCash, aDeposit }.Where(a => a.Id == 0).ToList();
    if (toAdd.Count > 0)
    {
        db.Accounts.AddRange(toAdd);
        await db.SaveChangesAsync(); // фиксируем Id счетов перед привязкой операций
    }

    if (!alreadySeeded)
    {
        // Привяжем часть демо-операций к счетам (покупки по кредитке — увеличивают долг)
        var demoTxs = db.Transactions.Where(t => t.UserId == demo.Id).ToList();
        foreach (var t in demoTxs)
        {
            if (t.Type == TxType.Income && t.CategoryId == cZarplata.Id) t.AccountId = aDebit.Id;      // зарплата на дебетовую
            else if (t.CategoryId == cFun.Id) t.AccountId = aCredit.Id;                                 // развлечения — в кредит
            else if (t.CategoryId == cAuto.Id) t.AccountId = aCash.Id;                                  // транспорт — наличными
            else if (t.Type == TxType.Expense) t.AccountId = aDebit.Id;                                  // остальное — основная карта
        }
        db.Transactions.Add(new Transaction { Category = cZarplata, UserId = demo.Id, Type = TxType.Income, Amount = 15000, Date = m.AddDays(5), Comment = "Погашение кредитной карты", AccountId = aCredit.Id });
    }

    // Семья: демо-пользователь — владелец семейной группы, приглашение принято
    if (string.IsNullOrEmpty(demo.FamilyCode)) demo.FamilyCode = "FAM-DEMO01";
    db.Entry(demo).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
    var spouse = db.Users.FirstOrDefault(u => u.Email == "spouse@schitayka.ru");
    if (spouse is null)
    {
        spouse = new User { Email = "spouse@schitayka.ru", Name = "Супруга", PasswordHash = SchitayKa.Api.Controllers.AuthController.HashPassword("spouse123"), FamilyCode = demo.FamilyCode };
        db.Users.Add(spouse);
        await db.SaveChangesAsync();
    }
    else spouse.FamilyCode = demo.FamilyCode;
    if (!db.FamilyInvitations.Any(i => i.OwnerUserId == demo.Id))
        db.FamilyInvitations.Add(new FamilyInvitation
        {
            OwnerUserId = demo.Id, Code = "INVITE-SP-01", Email = "spouse@schitayka.ru",
            ExpiresAt = DateTime.UtcNow.AddDays(7), AcceptedAt = DateTime.UtcNow, AcceptedByUserId = spouse.Id,
        });

    // Переводы между счетами (пары расход/доход): вклад, погашение кредитки, наличные
    void Transfer(string comment, decimal amount, Account from, Account to, int day)
    {
        db.Transactions.Add(new Transaction { Category = cOther, UserId = demo.Id, Type = TxType.Expense, Amount = amount, Date = m.AddDays(day), Comment = comment, AccountId = from.Id });
        db.Transactions.Add(new Transaction { Category = cOther, UserId = demo.Id, Type = TxType.Income, Amount = amount, Date = m.AddDays(day), Comment = comment, AccountId = to.Id });
    }
    if (!db.Transactions.Any(t => t.UserId == demo.Id && t.Comment.StartsWith("Перевод")))
    {
        Transfer("Перевод: пополнение вклада", 20000, aDebit, aDeposit, 6);
        Transfer("Перевод: погашение кредитной карты", 50000, aDebit, aCredit, 7);
        Transfer("Перевод: снятие наличных", 5000, aDebit, aCash, 8);
    }

    await db.SaveChangesAsync();
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
            // index.html — не кэшируем (иначе браузер после обновления покажет старый бандл);
            // assets с хешем в имени — можно кэшировать надолго.
            ctx.Response.Headers.CacheControl = candidate.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)
                ? "no-cache"
                : "public, max-age=31536000, immutable";
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
            ctx.Response.Headers.CacheControl = "no-cache";
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
