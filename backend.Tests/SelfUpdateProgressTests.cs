using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>
/// B21: корректность прогресса автообновления — шкала 0..100, монотонность шагов,
/// финальные 100 % только после успешного завершения и сброс при ошибке.
/// </summary>
public class SelfUpdateProgressTests
{
    private const string Target = "99.99.99";
    private const string Asset = "schitayka-99.99.99-app.zip";

    [Fact]
    public async Task Run_WithoutRestart_StepsAreMonotonicAndFinishAt100()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.CreateService();

        var started = await service.StartAsync(Target, restart: false);
        Assert.Equal(SelfUpdateStatus.Running, started.Status);
        Assert.Equal(0, started.Progress);

        var state = await WaitForCompletionAsync(service);

        Assert.Equal(SelfUpdateStatus.Done, state.Status);
        Assert.Equal(100, state.Progress);
        Assert.Null(state.Error);
        Assert.Equal(Target, state.TargetVersion);
        Assert.NotNull(state.StartedAt);
        Assert.NotNull(state.FinishedAt);

        var percents = ReadPercents(state.Log);
        Assert.NotEmpty(percents);
        Assert.Equal(100, percents[^1]);
        Assert.All(percents, p => Assert.InRange(p, 0, 100));
        for (var i = 1; i < percents.Count; i++)
            Assert.True(percents[i] >= percents[i - 1], $"Процент пошёл назад: {percents[i - 1]} → {percents[i]}");

        AssertStepsInOrder(state.Log,
            "Начинаю обновление",
            "Скачиваю schitayka",
            "Скачано 100%",
            "Проверяю контрольную сумму",
            "Распаковываю обновление",
            "Делаю резервную копию",
            "Заменяю файлы приложения",
            "Обновление применено");

        // Данные не должны заменяться файлами из архива.
        Assert.Equal("old-db", File.ReadAllText(Path.Combine(fixture.InstallDir, "data", "app.db")));
        Assert.Equal("new-api", File.ReadAllText(Path.Combine(fixture.InstallDir, "SchitayKa.Api.dll")));
    }

    [Fact]
    public async Task Run_WithRestart_Reaches100BeforeRestart()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.CreateService();
        service.RestartDelay = TimeSpan.Zero;

        var restarted = false;
        service.RestartProcess = () => restarted = true;

        await service.StartAsync(Target, restart: true);
        var state = await WaitForCompletionAsync(service);

        Assert.Equal(SelfUpdateStatus.Done, state.Status);
        Assert.Equal(100, state.Progress);
        Assert.True(restarted, "Делегат перезапуска должен быть вызван после применения обновления.");

        var log = string.Join('\n', state.Log);
        Assert.Contains("Перезапускаю приложение", log, StringComparison.Ordinal);
        Assert.Contains("Приложение перезапускается", log, StringComparison.Ordinal);
        Assert.True(log.IndexOf("Перезапускаю приложение", StringComparison.Ordinal)
                    < log.IndexOf("Приложение перезапускается", StringComparison.Ordinal));
        Assert.DoesNotContain("ОШИБКА:", log, StringComparison.Ordinal);
    }

    // ContentLength неизвестен (chunked): без фиксации завершения прогресс застревал на 5 %.
    [Fact]
    public async Task Run_UnknownContentLength_ReportsDownloadComplete()
    {
        using var fixture = new UpdateFixture(withContentLength: false);
        var service = fixture.CreateService();

        await service.StartAsync(Target, restart: false);
        var state = await WaitForCompletionAsync(service);

        Assert.Equal(SelfUpdateStatus.Done, state.Status);
        Assert.Equal(100, state.Progress);
        Assert.Contains(state.Log, line => line.Contains("Скачано 100%", StringComparison.Ordinal));
    }

    // Некорректный ContentLength сервера не должен давать процент больше 100.
    [Fact]
    public async Task Run_WrongContentLength_ClampsProgressTo100()
    {
        using var fixture = new UpdateFixture(contentLengthOverride: 1);
        var service = fixture.CreateService();

        await service.StartAsync(Target, restart: false);
        var state = await WaitForCompletionAsync(service);

        Assert.Equal(SelfUpdateStatus.Done, state.Status);
        Assert.Equal(100, state.Progress);
        Assert.All(ReadPercents(state.Log), p => Assert.InRange(p, 0, 100));
    }

    [Fact]
    public async Task Run_DownloadFailed_ResetsProgressAndSetsFinishedAt()
    {
        using var fixture = new UpdateFixture(artifactStatus: HttpStatusCode.NotFound);
        var service = fixture.CreateService();

        await service.StartAsync(Target, restart: true);
        var state = await WaitForCompletionAsync(service);

        Assert.Equal(SelfUpdateStatus.Failed, state.Status);
        Assert.Equal(0, state.Progress);
        Assert.NotNull(state.Error);
        Assert.Contains("404", state.Error, StringComparison.Ordinal);
        Assert.NotNull(state.FinishedAt);
        Assert.Contains(state.Log, line => line.Contains("ОШИБКА:", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Log, line => line.Contains("Обновление применено", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_TwoConcurrentCalls_StartsSingleUpdate()
    {
        using var fixture = new UpdateFixture(responseDelay: TimeSpan.FromMilliseconds(250));
        var service = fixture.CreateService();

        var first = await service.StartAsync(Target, restart: false);
        var second = await service.StartAsync(Target, restart: false);

        Assert.True(SelfUpdateService.IsRunning(second.Status));
        Assert.Same(first.TargetVersion, second.TargetVersion);

        var state = await WaitForCompletionAsync(service);
        Assert.Equal(SelfUpdateStatus.Done, state.Status);
        Assert.Equal(1, state.Log.Count(line => line.Contains("Начинаю обновление", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartAsync_InvalidVersion_FailsWithZeroProgress()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.CreateService();

        var state = await service.StartAsync("../../evil", restart: false);

        Assert.Equal(SelfUpdateStatus.Failed, state.Status);
        Assert.Equal(0, state.Progress);
        Assert.Contains("Некорректная версия", state.Error, StringComparison.Ordinal);
        Assert.NotNull(state.FinishedAt);
    }

    private static async Task<SelfUpdateState> WaitForCompletionAsync(SelfUpdateService service)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = service.State;
            if (!SelfUpdateService.IsRunning(state.Status)) return state;
            await Task.Delay(20);
        }
        return service.State;
    }

    private static List<int> ReadPercents(IReadOnlyList<string> log)
        => Regex.Matches(string.Join('\n', log), @"\[\s*(\d+)%\]")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();

    private static void AssertStepsInOrder(IReadOnlyList<string> log, params string[] fragments)
    {
        var text = string.Join('\n', log);
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var index = text.IndexOf(fragment, StringComparison.Ordinal);
            Assert.True(index >= 0, $"В журнале нет шага «{fragment}».");
            Assert.True(index > previous, $"Шаг «{fragment}» идёт не в том порядке.");
            previous = index;
        }
    }

    /// <summary>Временный каталог установки и поддельные ответы GitHub для полного цикла обновления.</summary>
    private sealed class UpdateFixture : IDisposable
    {
        public string InstallDir { get; }
        private readonly byte[] _zip;
        private readonly string _sha256;
        private readonly bool _withContentLength;
        private readonly long? _contentLengthOverride;
        private readonly HttpStatusCode _artifactStatus;
        private readonly TimeSpan _responseDelay;

        public UpdateFixture(
            bool withContentLength = true,
            long? contentLengthOverride = null,
            HttpStatusCode artifactStatus = HttpStatusCode.OK,
            TimeSpan responseDelay = default)
        {
            InstallDir = Path.Combine(Path.GetTempPath(), $"sk-b21-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(InstallDir, "data"));
            File.WriteAllText(Path.Combine(InstallDir, "SchitayKa.Api.dll"), "old-api");
            File.WriteAllText(Path.Combine(InstallDir, "data", "app.db"), "old-db");

            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("SchitayKa.Api.dll");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("new-api");
            }
            _zip = stream.ToArray();
            _sha256 = Convert.ToHexString(SHA256.HashData(_zip)).ToLowerInvariant();

            _withContentLength = withContentLength;
            _contentLengthOverride = contentLengthOverride;
            _artifactStatus = artifactStatus;
            _responseDelay = responseDelay;
        }

        public SelfUpdateService CreateService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SelfUpdate:Enabled"] = "true",
                    ["SelfUpdate:InstallDir"] = InstallDir,
                    ["Update:RepoUrl"] = "https://github.com/acme/repo",
                })
                .Build();

            var service = new SelfUpdateService(
                config,
                new FakeFactory(new HttpClient(new FakeHandler(
                    _zip, _sha256, _withContentLength, _contentLengthOverride, _artifactStatus, _responseDelay))),
                NullLogger<SelfUpdateService>.Instance);
            // На всякий случай подменяем выход из процесса, даже если тест не дошёл до перезапуска.
            service.RestartProcess = static () => { };
            service.RestartDelay = TimeSpan.Zero;
            return service;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); }
            catch { /* временные файлы удалятся позже */ }
        }

        private sealed class FakeFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public FakeFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly byte[] _zip;
            private readonly string _sha256;
            private readonly bool _withContentLength;
            private readonly long? _contentLengthOverride;
            private readonly HttpStatusCode _artifactStatus;
            private readonly TimeSpan _responseDelay;

            public FakeHandler(byte[] zip, string sha256, bool withContentLength, long? contentLengthOverride,
                HttpStatusCode artifactStatus, TimeSpan responseDelay)
            {
                _zip = zip;
                _sha256 = sha256;
                _withContentLength = withContentLength;
                _contentLengthOverride = contentLengthOverride;
                _artifactStatus = artifactStatus;
                _responseDelay = responseDelay;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_responseDelay > TimeSpan.Zero)
                    await Task.Delay(_responseDelay, cancellationToken);

                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith(".sha256", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{_sha256}  {Asset}\n", Encoding.UTF8),
                    };

                if (path.Contains("/releases/download/", StringComparison.Ordinal))
                {
                    if (_artifactStatus != HttpStatusCode.OK)
                        return new HttpResponseMessage(_artifactStatus);

                    var content = new ByteArrayContent(_zip);
                    if (_contentLengthOverride.HasValue)
                        content.Headers.ContentLength = _contentLengthOverride.Value;
                    else if (!_withContentLength)
                        content.Headers.ContentLength = null;

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
