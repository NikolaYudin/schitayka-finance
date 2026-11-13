using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

public enum SelfUpdateStatus
{
    Idle,
    Running,
    Downloading,
    Verifying,
    Extracting,
    BackingUp,
    Applying,
    Done,
    Failed,
    Restarting,
}

/// <summary>Состояние процесса самообновления (для UI и журнала).</summary>
public sealed record SelfUpdateState(
    SelfUpdateStatus Status,
    string? TargetVersion,
    int Progress,
    string? Error,
    bool Enabled,
    string? InstallDir,
    string? CurrentVersion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> Log)
{
    public static SelfUpdateState Idle(bool enabled, string? installDir, string? currentVersion) =>
        new(SelfUpdateStatus.Idle, null, 0, null, enabled, installDir, currentVersion, null, null, Array.Empty<string>());
}

/// <summary>
/// Автоматическое обновление приложения по кнопке (Задача 9).
///
/// Как это работает:
///   1. скачивает артефакт `schitayka-&lt;версия&gt;-app.zip` из GitHub Releases
///      (собранное приложение: dll + wwwroot);
///   2. проверяет SHA256 по одноимённому файлу `.sha256` (если он опубликован);
///   3. распаковывает во временную папку с защитой от «zip-slip»;
///   4. делает резервную копию текущих файлов;
///   5. заменяет файлы приложения на новые (папка `data` не трогается);
///   6. перезапускает процесс — в Docker контейнер поднимется заново
///      (restart: unless-stopped), база в томе schitayka-data сохраняется.
///
/// Обновление разрешено только администратору экземпляра:
///   - пользователю из списка `SelfUpdate:AdminEmails`, либо
///   - самому первому зарегистрированному пользователю.
/// Само обновление включается флагом `SelfUpdate:Enabled`.
/// </summary>
public sealed class SelfUpdateService
{
    private const string DefaultRepoUrl = "https://github.com/NikolaYudin/schitayka-finance";
    private const int MaxLogLines = 400;

    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SelfUpdateService> _log;
    private readonly AppDbContext _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly List<string> _logLines = [];
    private SelfUpdateState _state;
    private Task? _running;

    public SelfUpdateService(IConfiguration cfg, IHttpClientFactory http, ILogger<SelfUpdateService> log, AppDbContext db)
    {
        _cfg = cfg;
        _http = http;
        _log = log;
        _db = db;
        _state = SelfUpdateState.Idle(Enabled, InstallDir, AppVersion.Current.Version);
    }

    public bool Enabled => _cfg.GetValue<bool?>("SelfUpdate:Enabled") ?? false;
    public string? InstallDir
    {
        get
        {
            var configured = _cfg["SelfUpdate:InstallDir"];
            return string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured.Trim();
        }
    }

    public SelfUpdateState State
    {
        get
        {
            lock (_logLines) return _state with { Log = _logLines.ToArray() };
        }
    }

    /// <summary>Может ли этот пользователь запускать обновление.</summary>
    public async Task<bool> IsAdminAsync(int uid, string? email)
    {
        var list = (_cfg["SelfUpdate:AdminEmails"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length > 0 && email is not null
            && list.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Первый зарегистрированный пользователь — администратор экземпляра.
        var firstId = await _db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        return firstId is int id && id == uid;
    }

    /// <summary>Запускает обновление в фоне. Возвращает текущее состояние процесса.</summary>
    public Task<SelfUpdateState> StartAsync(string? version, bool restart)
    {
        lock (_logLines)
        {
            if (_state.Status is SelfUpdateStatus.Running or SelfUpdateStatus.Downloading
                or SelfUpdateStatus.Applying or SelfUpdateStatus.Restarting)
                return Task.FromResult(State);

            _logLines.Clear();
            _running = Task.Run(() => RunAsync(version, restart));
            return Task.FromResult(State);
        }
    }

    private async Task RunAsync(string? version, bool restart)
    {
        var started = DateTimeOffset.UtcNow;
        void Step(SelfUpdateStatus status, int progress, string message)
        {
            lock (_logLines)
            {
                _logLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
                _state = _state with { Status = status, Progress = progress, TargetVersion = version ?? _state.TargetVersion };
            }
            _log.LogInformation("[self-update] {Message}", message);
        }

        try
        {
            var installDir = InstallDir;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                throw new InvalidOperationException("Каталог установки не найден.");

            var repoUrl = (_cfg["Update:RepoUrl"] ?? DefaultRepoUrl).Trim().TrimEnd('/');
            if (repoUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repoUrl = repoUrl[..^4];
            var assetTemplate = _cfg["SelfUpdate:ReleaseAsset"] ?? "schitayka-{version}-app.zip";

            var target = version?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                // Версию берём из последнего релиза GitHub.
                target = await ResolveLatestVersionAsync(repoUrl);
            }
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("Не удалось определить версию обновления.");

            var assetName = assetTemplate.Replace("{version}", target, StringComparison.OrdinalIgnoreCase);
            var downloadUrl = $"{repoUrl}/releases/download/v{target}/{assetName}";

            Step(SelfUpdateStatus.Running, 2, $"Начинаю обновление до версии {target}.");
            Step(SelfUpdateStatus.Downloading, 5, $"Скачиваю {assetName}…");

            var tempZip = Path.Combine(Path.GetTempPath(), $"schitayka-update-{target}-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(downloadUrl, tempZip, p => Step(SelfUpdateStatus.Downloading, 5 + p * 45, $"Скачано {p}%."), CancellationToken.None);

                Step(SelfUpdateStatus.Verifying, 55, "Проверяю контрольную сумму…");
                await VerifyChecksumAsync(downloadUrl + ".sha256", tempZip);

                Step(SelfUpdateStatus.Extracting, 65, "Распаковываю обновление…");
                var tempDir = Path.Combine(Path.GetTempPath(), $"schitayka-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var files = ExtractSafe(tempZip, tempDir);
                    if (!File.Exists(Path.Combine(tempDir, "SchitayKa.Api.dll")))
                        throw new InvalidOperationException("В архиве нет SchitayKa.Api.dll — это не сборка приложения.");

                    Step(SelfUpdateStatus.BackingUp, 75, "Делаю резервную копию текущих файлов…");
                    var backupDir = Path.Combine(installDir, $".backup-{target}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    CopyDirectory(installDir, backupDir, skipData: true);
                    Step(SelfUpdateStatus.BackingUp, 80, $"Резервная копия: {Path.GetFileName(backupDir)}");

                    Step(SelfUpdateStatus.Applying, 85, "Заменяю файлы приложения…");
                    var applied = CopyDirectory(tempDir, installDir, skipData: true);
                    Step(SelfUpdateStatus.Applying, 95, $"Обновлено файлов: {applied}. База данных не тронута.");

                    if (restart)
                    {
                        Step(SelfUpdateStatus.Restarting, 98, "Перезапускаю приложение…");
                        _state = _state with { Status = SelfUpdateStatus.Restarting, Progress = 98 };
                        // Даём время отдать ответ, затем перезапускаем процесс:
                        // в Docker контейнер поднимется заново (restart: unless-stopped).
                        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                        {
                            _log.LogWarning("[self-update] перезапуск процесса после обновления");
                            Environment.Exit(0);
                        });
                    }
                    else
                    {
                        Step(SelfUpdateStatus.Done, 100, "Обновление применено. Перезапустите приложение вручную.");
                    }
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
                }
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[self-update] не удалось применить обновление");
            lock (_logLines)
            {
                _logLines.Add($"{DateTime.Now:HH:mm:ss}  ОШИБКА: {ex.Message}");
                _state = _state with
                {
                    Status = SelfUpdateStatus.Failed,
                    Error = ex.Message,
                    Progress = 0,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.Now,
                };
            }
        }
    }

    private async Task<string?> ResolveLatestVersionAsync(string repoUrl)
    {
        try
        {
            var client = _http.CreateClient("github");
            var parts = repoUrl.Split('/');
            if (parts.Length < 2) return null;
            var owner = parts[^2];
            var repo = parts[^1];
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("SchitayKa-SelfUpdate/1.0");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, CancellationToken.None);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            return tag?.TrimStart('v');
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadAsync(string url, string destPath, Action<int> onProgress, CancellationToken ct)
    {
        var client = _http.CreateClient("github-download");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Сервер GitHub вернул {(int)response.StatusCode}. Возможно, для этой версии нет собранного артефакта.");

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        int lastPercent = -1;
        while (true)
        {
            var n = await src.ReadAsync(buffer, ct);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                var pct = (int)(read * 100 / total);
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    onProgress(pct);
                }
            }
        }
    }

    /// <summary>Сверяет SHA256 с опубликованным файлом `.sha256` (если он есть).</summary>
    private async Task VerifyChecksumAsync(string checksumUrl, string filePath)
    {
        try
        {
            var client = _http.CreateClient("github-download");
            using var response = await client.GetAsync(checksumUrl);
            if (!response.IsSuccessStatusCode) return; // контрольной суммы нет — не критично
            var text = await response.Content.ReadAsStringAsync();
            var expected = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(t => t.Length == 64);
            if (expected is null) return;
            var actual = Sha256Hex(filePath);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма не совпадает — обновление отменено.");
        }
        catch (HttpRequestException)
        {
            // сеть недоступна для проверки — не блокируем обновление
        }
    }

    public static string Sha256Hex(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Безопасная распаковка: путь внутри архива не должен выходить за пределы каталога
    /// (защита от «zip slip»).
    /// </summary>
    public static int ExtractSafe(string zipPath, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // каталог
            var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException($"Некорректный путь в архиве: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            count++;
        }
        return count;
    }

    /// <summary>Копирует файлы (без папки `data`), возвращает число скопированных файлов.</summary>
    private static int CopyDirectory(string from, string to, bool skipData)
    {
        var count = 0;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (skipData && IsSkipped(rel)) continue;
            var target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            count++;
        }
        return count;
    }

    /// <summary>Папки, которые нельзя перезаписывать при обновлении (данные и бэкапы).</summary>
    public static bool IsSkipped(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.StartsWith(".backup-", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
