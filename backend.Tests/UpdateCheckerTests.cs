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

    private static UpdateChecker CreateChecker(
        string commitsJson, string? version, HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Update:RepoUrl"] = "https://github.com/acme/repo",
            })
            .Build();

        var handler = new FakeHandler(commitsJson, version, status);
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

        public FakeHandler(string commitsJson, string? version, HttpStatusCode status)
        {
            _commitsJson = commitsJson;
            _version = version;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "";

            if (host == "api.github.com")
            {
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
