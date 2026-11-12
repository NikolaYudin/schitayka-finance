using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SchitayKa.Api.Services;

public sealed record LatestCommit(
    string Sha,
    string ShortSha,
    string Message,
    string Url,
    DateTimeOffset? Date,
    string? Version);

public sealed record UpdateCheckResult(
    bool Ok,
    string? Error,
    CurrentVersion Current,
    LatestCommit? Latest,
    bool UpdateAvailable,
    string RepoUrl,
    string? CompareUrl,
    DateTimeOffset CheckedAt);

/// <summary>
/// Проверяет последний коммит в GitHub-репозитории и сравнивает его с текущей сборкой.
/// Результат кэшируется на 10 минут, чтобы не исчерпывать лимит GitHub API.
/// </summary>
public sealed class UpdateChecker
{
    private const string DefaultRepoUrl = "https://github.com/NikolaYudin/schitayka-finance";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<UpdateChecker> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateCheckResult? _cached;
    private DateTimeOffset _cachedAt;

    public UpdateChecker(IConfiguration cfg, IHttpClientFactory http, ILogger<UpdateChecker> log)
    {
        _cfg = cfg;
        _http = http;
        _log = log;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var result = await FetchAsync(ct);
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCheckResult> FetchAsync(CancellationToken ct)
    {
        var current = AppVersion.Current;
        var checkedAt = DateTimeOffset.UtcNow;
        var repoUrl = (_cfg["Update:RepoUrl"] ?? DefaultRepoUrl).Trim().TrimEnd('/');
        if (repoUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoUrl = repoUrl[..^4];

        if (!TryParseRepo(repoUrl, out var owner, out var repo))
            return Fail("Некорректный адрес репозитория в Update:RepoUrl.", current, repoUrl, checkedAt);

        try
        {
            var client = _http.CreateClient("github");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/commits?per_page=1");
            request.Headers.UserAgent.ParseAdd("SchitayKa-VersionCheck/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "GitHub ограничил запросы (rate limit). Попробуйте позже или задайте GITHUB_TOKEN.",
                    HttpStatusCode.NotFound => "Репозиторий не найден — проверьте Update:RepoUrl.",
                    _ => $"GitHub API вернул {(int)response.StatusCode}.",
                };
                return Fail(error, current, repoUrl, checkedAt);
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return Fail("В репозитории нет коммитов.", current, repoUrl, checkedAt);

            var first = doc.RootElement[0];
            var sha = first.TryGetProperty("sha", out var shaEl) ? shaEl.GetString() ?? "" : "";
            if (sha.Length == 0)
                return Fail("GitHub вернул коммит без SHA.", current, repoUrl, checkedAt);

            var commitEl = first.TryGetProperty("commit", out var c) ? c : default;
            var message = commitEl.ValueKind == JsonValueKind.Object && commitEl.TryGetProperty("message", out var m)
                ? m.GetString() ?? ""
                : "";
            DateTimeOffset? date = null;
            if (commitEl.ValueKind == JsonValueKind.Object
                && commitEl.TryGetProperty("committer", out var committer)
                && committer.TryGetProperty("date", out var dateEl)
                && DateTimeOffset.TryParse(dateEl.GetString(), out var parsedDate))
            {
                date = parsedDate;
            }

            var url = first.TryGetProperty("html_url", out var urlEl) && urlEl.GetString() is { Length: > 0 } u
                ? u
                : $"{repoUrl}/commits";

            var shortSha = sha.Length > 7 ? sha[..7] : sha;
            var firstLine = message.Split('\n')[0].Trim();
            if (firstLine.Length > 180) firstLine = firstLine[..180] + "…";

            var latestVersion = await TryReadVersionAsync(client, owner, repo, ct);

            var currentKnown = !string.Equals(current.Commit, "unknown", StringComparison.OrdinalIgnoreCase)
                               && current.Commit.Length >= 4;
            var same = currentKnown
                       && (sha.StartsWith(current.Commit, StringComparison.OrdinalIgnoreCase)
                           || current.Commit.StartsWith(sha, StringComparison.OrdinalIgnoreCase));

            var available = currentKnown
                ? !same && (date is null || current.BuildDate is null || date.Value.UtcDateTime > current.BuildDate.Value)
                : latestVersion is not null && IsNewerVersion(current.Version, latestVersion)
                    ? true
                    : current.BuildDate is { } bd
                        ? date is null || date.Value.UtcDateTime > bd
                        : true;

            var compareUrl = currentKnown
                ? $"{repoUrl}/compare/{current.Commit}...{sha}"
                : $"{repoUrl}/commits";

            return new UpdateCheckResult(
                Ok: true,
                Error: null,
                Current: current,
                Latest: new LatestCommit(sha, shortSha, firstLine, url, date, latestVersion),
                UpdateAvailable: available,
                RepoUrl: repoUrl,
                CompareUrl: compareUrl,
                CheckedAt: checkedAt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("GitHub не ответил за 8 секунд.", current, repoUrl, checkedAt);
        }
        catch (HttpRequestException ex)
        {
            SafeLog(ex, "GitHub update check failed");
            return Fail("Не удалось связаться с GitHub. Проверьте интернет на NAS.", current, repoUrl, checkedAt);
        }
        catch (JsonException ex)
        {
            SafeLog(ex, "GitHub update check returned invalid JSON");
            return Fail("GitHub вернул неожиданный ответ.", current, repoUrl, checkedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeLog(ex, "Unexpected GitHub update check failure");
            return Fail("Не удалось проверить обновления. Подробности в логах сервера.", current, repoUrl, checkedAt);
        }
    }

    /// <summary>Логирование не должно ронять запрос, если провайдер логов недоступен.</summary>
    private void SafeLog(Exception ex, string message)
    {
        try
        {
            _log.LogWarning(ex, "{Message}", message);
        }
        catch
        {
            // Например, EventLog недоступен в ограниченном окружении.
        }
    }

    private static async Task<string?> TryReadVersionAsync(
        HttpClient client, string owner, string repo, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await client.GetAsync(
                $"https://raw.githubusercontent.com/{owner}/{repo}/main/VERSION",
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var text = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim();
            return text.Length is > 0 and <= 40 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static UpdateCheckResult Fail(
        string error, CurrentVersion current, string repoUrl, DateTimeOffset checkedAt)
        => new(
            Ok: false,
            Error: error,
            Current: current,
            Latest: null,
            UpdateAvailable: false,
            RepoUrl: repoUrl,
            CompareUrl: null,
            CheckedAt: checkedAt);

    private static bool TryParseRepo(string url, out string owner, out string repo)
    {
        owner = repo = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        owner = parts[0];
        repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        return owner.Length > 0 && repo.Length > 0;
    }

    private static bool IsNewerVersion(string current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        // Отбрасываем semver-суффикс (-beta) — для сравнения достаточно numeric major.minor.patch.
        var currentCore = current.Split('-')[0];
        var latestCore = latest.Split('-')[0];
        return Version.TryParse(latestCore, out var latestVer)
               && Version.TryParse(currentCore, out var currentVer)
               && latestVer > currentVer;
    }
}
