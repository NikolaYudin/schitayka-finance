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

/// <summary>
/// Единая шкала прогресса автообновления (задача B21). Значения зафиксированы по шагам:
/// процент не зависит от скорости сети и всегда укладывается в диапазон 0..100.
/// </summary>
public static class SelfUpdateProgress
{
    public const int Start = 0;
    public const int Running = 2;
    public const int DownloadingFrom = 5;
    public const int DownloadingTo = 50;
    public const int Verifying = 55;
    public const int Extracting = 65;
    public const int Extracted = 68;
    public const int BackingUpFrom = 70;
    public const int BackingUpTo = 78;
    public const int ApplyingFrom = 82;
    public const int ApplyingTo = 95;
    public const int Restarting = 98;
    public const int Done = 100;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Один общий мьютекс для состояния и журнала: раньше роль мьютекса выполнял список логов,
    // из-за чего часть обновлений состояния шла вне блокировки (гонка на шаге Restarting).
    private readonly object _sync = new();

    private readonly List<string> _logLines = [];
    private SelfUpdateState _state;
    private Task? _running;

    /// <summary>
    /// Перезапуск процесса вынесен в свойство, чтобы тесты не завершали тестовый раннер.
    /// В рабочем режиме это Environment.Exit(0).
    /// </summary>
    public Action RestartProcess { get; set; } = static () => Environment.Exit(0);

    /// <summary>Задержка перед перезапуском: успеваем отдать ответ клиенту.</summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(2);

    public SelfUpdateService(IConfiguration cfg, IHttpClientFactory http, ILogger<SelfUpdateService> log)
    {
        _cfg = cfg;
        _http = http;
        _log = log;
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
            lock (_sync) return Snapshot();
        }
    }

    /// <summary>Снимок состояния; вызывается только под блокировкой _sync.</summary>
    private SelfUpdateState Snapshot() => _state with { Log = _logLines.ToArray() };

    /// <summary>Добавляет строку в журнал; вызывается только под блокировкой _sync.</summary>
    private void AppendLog(string message)
    {
        _logLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
    }

    /// <summary>Признак активного процесса обновления (используется и контроллером).</summary>
    public static bool IsRunning(SelfUpdateStatus status) => status is
        SelfUpdateStatus.Running or SelfUpdateStatus.Downloading or SelfUpdateStatus.Verifying
        or SelfUpdateStatus.Extracting or SelfUpdateStatus.BackingUp or SelfUpdateStatus.Applying
        or SelfUpdateStatus.Restarting;

    /// <summary>
    /// Разрешён ли запуск обновления этому пользователю:
    /// e-mail из списка администраторов. Проверка «первый пользователь» — в контроллере.
    /// </summary>
    public bool IsAdminByEmail(string? email)
    {
        var list = (_cfg["SelfUpdate:AdminEmails"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0 || email is null) return false;
        return list.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Запускает обновление в фоне. Возвращает текущее состояние процесса.</summary>
    public Task<SelfUpdateState> StartAsync(string? version, bool restart)
    {
        var target = string.IsNullOrWhiteSpace(version) ? null : version.Trim();

        // Версия попадает в URL загрузки и в имя каталога резервной копии, поэтому
        // допускаем только цифры и точки — иначе «..\..» выводит запись за каталог.
        if (target is not null && !System.Text.RegularExpressions.Regex.IsMatch(target, @"^[0-9]+(\.[0-9]+){0,3}$"))
        {
            lock (_sync)
            {
                _logLines.Clear();
                AppendLog($"Некорректная версия: {target}");
                var now = DateTimeOffset.UtcNow;
                _state = _state with
                {
                    Status = SelfUpdateStatus.Failed,
                    TargetVersion = target,
                    Progress = SelfUpdateProgress.Start,
                    Error = "Некорректная версия обновления: допустимы цифры и точки (например, 0.2.3).",
                    StartedAt = now,
                    FinishedAt = now,
                };
            }
            return Task.FromResult(State);
        }

        // Обновление на уже установленную версию не запускаем: иначе кнопка «Обновить сейчас»
        // повторно скачивала бы тот же app.zip и перезаписывала файлы.
        if (target is not null && AppVersion.IsSame(AppVersion.Current.Version, target))
        {
            lock (_sync)
            {
                _logLines.Clear();
                AppendLog($"Версия {target} уже установлена — обновление не требуется.");
                var now = DateTimeOffset.UtcNow;
                _state = _state with
                {
                    Status = SelfUpdateStatus.Done,
                    TargetVersion = target,
                    Progress = SelfUpdateProgress.Done,
                    Error = null,
                    StartedAt = now,
                    FinishedAt = now,
                };
            }
            return Task.FromResult(State);
        }

        lock (_sync)
        {
            if (IsRunning(_state.Status)) return Task.FromResult(State);

            _logLines.Clear();
            // Статус Running выставляем до возврата из StartAsync: иначе второй клик
            // видел Idle и запускал параллельное обновление.
            _state = _state with
            {
                Status = SelfUpdateStatus.Running,
                TargetVersion = target,
                Progress = SelfUpdateProgress.Start,
                Error = null,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = null,
            };
            _running = Task.Run(() => RunAsync(target, restart));
            return Task.FromResult(Snapshot());
        }
    }

    /// <summary>Обёртка с семафором: одновременно выполняется только одно обновление.</summary>
    private async Task RunAsync(string? version, bool restart)
    {
        await _gate.WaitAsync();
        try
        {
            await RunCoreAsync(version, restart);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunCoreAsync(string? version, bool restart)
    {
        var started = DateTimeOffset.UtcNow;
        // Версия может быть определена из последнего релиза GitHub — шаги должны её видеть.
        string? target = version?.Trim();

        void Step(SelfUpdateStatus status, int progress, string message)
        {
            lock (_sync)
            {
                var value = Math.Clamp(Math.Max(progress, _state.Progress), SelfUpdateProgress.Start, SelfUpdateProgress.Done);
                AppendLog($"[{value,3}%] {message}");
                _state = _state with { Status = status, Progress = value, TargetVersion = target ?? _state.TargetVersion };
            }
            _log.LogInformation("[self-update] {Message}", message);
        }

        // Финальный шаг: 100 выставляется только после успешного завершения всех работ.
        void Finish(string message)
        {
            lock (_sync)
            {
                AppendLog($"[{SelfUpdateProgress.Done,3}%] {message}");
                _state = _state with
                {
                    Status = SelfUpdateStatus.Done,
                    Progress = SelfUpdateProgress.Done,
                    Error = null,
                    TargetVersion = target ?? _state.TargetVersion,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow,
                };
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

            if (string.IsNullOrWhiteSpace(target))
            {
                // Версию берём из последнего релиза GitHub.
                target = await ResolveLatestVersionAsync(repoUrl);
            }
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("Не удалось определить версию обновления.");

            target = target.Trim();
            var assetName = assetTemplate.Replace("{version}", target, StringComparison.OrdinalIgnoreCase);
            var downloadUrl = $"{repoUrl}/releases/download/v{target}/{assetName}";

            Step(SelfUpdateStatus.Running, SelfUpdateProgress.Running, $"Начинаю обновление до версии {target}.");
            Step(SelfUpdateStatus.Downloading, SelfUpdateProgress.DownloadingFrom, $"Скачиваю {assetName}…");

            var tempZip = Path.Combine(Path.GetTempPath(), $"schitayka-update-{target}-{Guid.NewGuid():N}.zip");
            try
            {
                // Диапазон загрузки занимает фиксированную часть шкалы; процент считаем от ContentLength.
                await DownloadAsync(downloadUrl, tempZip, p => Step(
                    SelfUpdateStatus.Downloading,
                    SelfUpdateProgress.DownloadingFrom + p * (SelfUpdateProgress.DownloadingTo - SelfUpdateProgress.DownloadingFrom) / 100,
                    $"Скачано {p}%."), CancellationToken.None);

                Step(SelfUpdateStatus.Verifying, SelfUpdateProgress.Verifying, "Проверяю контрольную сумму…");
                await VerifyChecksumAsync(downloadUrl + ".sha256", tempZip);

                Step(SelfUpdateStatus.Extracting, SelfUpdateProgress.Extracting, "Распаковываю обновление…");
                var tempDir = Path.Combine(Path.GetTempPath(), $"schitayka-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var files = ExtractSafe(tempZip, tempDir);
                    if (!File.Exists(Path.Combine(tempDir, "SchitayKa.Api.dll")))
                        throw new InvalidOperationException("В архиве нет SchitayKa.Api.dll — это не сборка приложения.");
                    Step(SelfUpdateStatus.Extracting, SelfUpdateProgress.Extracted, $"Распаковано файлов: {files}.");

                    Step(SelfUpdateStatus.BackingUp, SelfUpdateProgress.BackingUpFrom, "Делаю резервную копию текущих файлов…");
                    var backupDir = Path.Combine(installDir, $".backup-{target}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    CopyDirectory(installDir, backupDir, skipData: true);
                    Step(SelfUpdateStatus.BackingUp, SelfUpdateProgress.BackingUpTo, $"Резервная копия: {Path.GetFileName(backupDir)}");

                    Step(SelfUpdateStatus.Applying, SelfUpdateProgress.ApplyingFrom, "Заменяю файлы приложения…");
                    var applied = CopyDirectory(tempDir, installDir, skipData: true);
                    Step(SelfUpdateStatus.Applying, SelfUpdateProgress.ApplyingTo, $"Обновлено файлов: {applied}. База данных не тронута.");

                    if (restart)
                    {
                        Step(SelfUpdateStatus.Restarting, SelfUpdateProgress.Restarting, "Перезапускаю приложение…");
                        // Даём время отдать ответ, затем перезапускаем процесс:
                        // в Docker контейнер поднимется заново (restart: unless-stopped).
                        _ = Task.Delay(RestartDelay).ContinueWith(_ =>
                        {
                            _log.LogWarning("[self-update] перезапуск процесса после обновления");
                            RestartProcess();
                        }, TaskScheduler.Default);
                        // Все шаги завершены, перезапуск запланирован: фиксируем успех,
                        // чтобы интерфейс успел показать 100 % до выхода процесса.
                        Finish($"Обновление до версии {target} применено. Приложение перезапускается…");
                    }
                    else
                    {
                        Finish("Обновление применено. Перезапустите приложение вручную.");
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
            lock (_sync)
            {
                AppendLog($"ОШИБКА: {ex.Message}");
                // Сброс: достигнутый процент больше не описывает состояние процесса.
                _state = _state with
                {
                    Status = SelfUpdateStatus.Failed,
                    Error = ex.Message,
                    Progress = SelfUpdateProgress.Start,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow,
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
        var lastPercent = 0;
        while (true)
        {
            var n = await src.ReadAsync(buffer, ct);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            // ContentLength может отсутствовать или расходиться с фактическим размером:
            // ограничиваем процент 0..100 и не даём ему идти назад.
            var pct = total > 0 ? (int)Math.Clamp(read * 100 / total, 0, 100) : 0;
            if (pct > lastPercent)
            {
                lastPercent = pct;
                onProgress(pct);
            }
        }

        // Фиксируем завершение загрузки даже при неизвестной длине (chunked),
        // иначе прогресс застревал на первом шаге скачивания.
        onProgress(100);
    }

    /// <summary>
    /// Сверяет SHA256 с опубликованным файлом `.sha256`.
    /// Недоступная или пустая контрольная сумма — ошибка: fail-open позволял применить
    /// непроверенный архив при 404 или сетевой ошибке.
    /// </summary>
    private async Task VerifyChecksumAsync(string checksumUrl, string filePath)
    {
        var client = _http.CreateClient("github-download");
        using var response = await client.GetAsync(checksumUrl);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Контрольная сумма недоступна (HTTP {(int)response.StatusCode}) — обновление отменено.");

        var text = await response.Content.ReadAsStringAsync();
        var expected = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(t => t.Length == 64);
        if (expected is null)
            throw new InvalidOperationException("Файл контрольной суммы пуст или повреждён — обновление отменено.");

        var actual = Sha256Hex(filePath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма не совпадает — обновление отменено.");
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
