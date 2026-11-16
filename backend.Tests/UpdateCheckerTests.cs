using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

// Проверка разбора ответа GitHub и вычисления доступности обновления без сети.
public class UpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_ParsesCommitAndVersion()
    {
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var json = $$"""
        [
          {
            "sha": "{{sha}}",
            "html_url": "https://github.com/acme/repo/commit/abcdef1",
            "commit": {
              "message": "Test update\n\nDetails",
              "committer": { "date": "2030-01-01T00:00:00Z" }
            }
          }
        ]
        """;

        var checker = CreateChecker(json, "1.2.0");
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(result.Latest);
        Assert.Equal(sha, result.Latest!.Sha);
        Assert.Equal("abcdef1", result.Latest.ShortSha);
        Assert.Equal("Test update", result.Latest.Message);
        Assert.Equal("1.2.0", result.Latest.Version);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("https://github.com/acme/repo", result.RepoUrl);
        Assert.Contains("compare", result.CompareUrl);
    }

    [Fact]
    public async Task CheckAsync_GitHubError_ReturnsFriendlyResult()
    {
        var checker = CreateChecker("[]", null, HttpStatusCode.Forbidden);
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("rate limit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UpdateAvailable);
    }

    // N1: коммит новее, но релиз (и VERSION в main) — та же версия → «обновление» не предлагаем.
    [Fact]
    public async Task CheckAsync_SameVersion_DoesNotOfferUpdate()
    {
        var sha = "ffffffffffffffffffffffffffffffffffffffff";
        var json = $$"""
        [
          {
            "sha": "{{sha}}",
            "html_url": "https://github.com/acme/repo/commit/fffffff",
            "commit": { "message": "docs: план", "committer": { "date": "2030-01-01T00:00:00Z" } }
          }
        ]
        """;

        var checker = CreateChecker(json, AppVersion.Current.Version);
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_NewerRelease_IsAvailable()
    {
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var json = $$"""
        [
          {
            "sha": "{{sha}}",
            "html_url": "https://github.com/acme/repo/commit/abcdef1",
            "commit": { "message": "release: 9.9.9", "committer": { "date": "2030-01-01T00:00:00Z" } }
          }
        ]
        """;
        var release = """{ "tag_name": "v9.9.9", "html_url": "https://github.com/acme/repo/releases/tag/v9.9.9" }""";

        var checker = CreateChecker(json, AppVersion.Current.Version, release: release);
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_SameReleaseVersion_IsNotAvailable()
    {
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var json = $$"""
        [
          {
            "sha": "{{sha}}",
            "html_url": "https://github.com/acme/repo/commit/abcdef1",
            "commit": { "message": "docs: план", "committer": { "date": "2030-01-01T00:00:00Z" } }
          }
        ]
        """;
        var tag = "v" + AppVersion.Current.Version;
        var release = $$"""{ "tag_name": "{{tag}}", "html_url": "https://github.com/acme/repo/releases/tag/{{tag}}" }""";

        var checker = CreateChecker(json, AppVersion.Current.Version, release: release);
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_OlderRelease_IsNotAvailable()
    {
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var json = $$"""
        [
          {
            "sha": "{{sha}}",
            "html_url": "https://github.com/acme/repo/commit/abcdef1",
            "commit": { "message": "release: 0.0.1", "committer": { "date": "2030-01-01T00:00:00Z" } }
          }
        ]
        """;
        var release = """{ "tag_name": "v0.0.1", "html_url": "https://github.com/acme/repo/releases/tag/v0.0.1" }""";

        var checker = CreateChecker(json, AppVersion.Current.Version, release: release);
        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.UpdateAvailable);
    }

    private static UpdateChecker CreateChecker(
        string commitsJson,
        string? version,
        HttpStatusCode status = HttpStatusCode.OK,
        string? release = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Update:RepoUrl"] = "https://github.com/acme/repo",
            })
            .Build();

        var handler = new FakeHandler(commitsJson, version, status, release);
        var factory = new FakeFactory(new HttpClient(handler));
        return new UpdateChecker(config, factory, NullLogger<UpdateChecker>.Instance);
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _commitsJson;
        private readonly string? _version;
        private readonly HttpStatusCode _status;
        private readonly string? _release;

        public FakeHandler(string commitsJson, string? version, HttpStatusCode status, string? release)
        {
            _commitsJson = commitsJson;
            _version = version;
            _status = status;
            _release = release;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "";
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (host == "api.github.com")
            {
                if (path.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
                {
                    if (_release is null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_release, Encoding.UTF8, "application/json"),
                    });
                }

                var response = new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_commitsJson, Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(response);
            }

            if (host == "raw.githubusercontent.com")
            {
                if (_version is null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_version, Encoding.UTF8, "text/plain"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
