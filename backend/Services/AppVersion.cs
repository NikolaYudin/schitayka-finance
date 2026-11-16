using System.Reflection;

namespace SchitayKa.Api.Services;

/// <summary>Версия приложения, зашитая в сборку (VERSION + commit из SourceRevisionId).</summary>
public record CurrentVersion(string Version, string Commit, string ShortCommit, DateTime? BuildDate);

public static class AppVersion
{
    public static CurrentVersion Current { get; } = Read();

    private static CurrentVersion Read()
    {
        var asm = Assembly.GetExecutingAssembly();

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";

        // SDK добавляет "+<commit>" к informational version, если известен SourceRevisionId.
        var plus = info.IndexOf('+');
        var version = (plus > 0 ? info[..plus] : info).Trim();
        var commit = plus > 0 ? info[(plus + 1)..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(commit)) commit = "unknown";

        var shortCommit = commit.Length > 12 ? commit[..12] : commit;

        DateTime? buildDate = null;
        try
        {
            var location = asm.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
                buildDate = File.GetLastWriteTimeUtc(location);
        }
        catch
        {
            // В single-file/нестандартной сборке Location может быть пустым.
        }

        return new CurrentVersion(version, commit, shortCommit, buildDate);
    }

    /// <summary>Отбрасывает semver-суффикс (-beta) — для сравнения достаточно major.minor.patch.</summary>
    private static string Core(string version) => version.Split('-')[0].Trim();

    /// <summary>Равны ли версии (без учёта semver-суффикса и ведущего «v»).</summary>
    public static bool IsSame(string current, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        var a = Core(current).TrimStart('v', 'V');
        var b = Core(other).TrimStart('v', 'V');
        return Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va == vb;
    }

    /// <summary>Является ли <paramref name="latest"/> более новой версией, чем <paramref name="current"/>.</summary>
    public static bool IsNewer(string current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        var a = Core(current).TrimStart('v', 'V');
        var b = Core(latest).TrimStart('v', 'V');
        return Version.TryParse(b, out var latestVer)
               && Version.TryParse(a, out var currentVer)
               && latestVer > currentVer;
    }
}
