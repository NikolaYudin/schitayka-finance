using System.IO.Compression;
using System.Text;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>Автоматическое обновление (Задача 9): безопасная распаковка и контроль целостности.</summary>
public class SelfUpdateTests
{
    private static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sk-selftest-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeZip(string dir, params (string EntryName, string Content)[] files)
    {
        var zipPath = Path.Combine(dir, "test.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return zipPath;
    }

    [Fact]
    public void ExtractSafe_UnpacksFilesToTargetDirectory()
    {
        var dir = TempDir("ok");
        try
        {
            var zip = MakeZip(dir,
                ("SchitayKa.Api.dll", "binary"),
                ("wwwroot/index.html", "<html>"));
            var dest = Path.Combine(dir, "out");

            var count = SelfUpdateService.ExtractSafe(zip, dest);

            Assert.Equal(2, count);
            Assert.True(File.Exists(Path.Combine(dest, "SchitayKa.Api.dll")));
            Assert.True(File.Exists(Path.Combine(dest, "wwwroot", "index.html")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExtractSafe_RejectsPathOutsideTargetDirectory()
    {
        var dir = TempDir("slip");
        try
        {
            var zip = MakeZip(dir, ("../escape.txt", "evil"));
            var dest = Path.Combine(dir, "out");

            var ex = Assert.Throws<InvalidOperationException>(() => SelfUpdateService.ExtractSafe(zip, dest));

            Assert.Contains("Некорректный путь", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "escape.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sha256Hex_MatchesKnownValue()
    {
        var dir = TempDir("sha");
        try
        {
            var file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, "abc", new UTF8Encoding(false));

            // SHA256("abc")
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                SelfUpdateService.Sha256Hex(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("data/app.db", true)]
    [InlineData("data/app.db-wal", true)]
    [InlineData("data", true)]
    [InlineData(".backup-0.2.0-20261113/app.db", true)]
    [InlineData("SchitayKa.Api.dll", false)]
    [InlineData("wwwroot/index.html", false)]
    [InlineData("runtimes/linux-x64/lib.so", false)]
    public void IsSkipped_ProtectsDataAndBackups(string path, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsSkipped(path));
    }
}
