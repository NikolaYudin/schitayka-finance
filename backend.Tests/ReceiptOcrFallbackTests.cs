using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>
/// Выбор OCR-провайдера и fallback на локальный tesseract (B2).
/// Сеть и бинарник tesseract не нужны — провайдеры подменяются заглушками.
/// </summary>
public class ReceiptOcrFallbackTests
{
    private sealed class FakeLocal : ILocalOcrProvider
    {
        public int Calls { get; private set; }
        public OcrProviderResult Result { get; set; } =
            OcrProviderResult.Success("текст из tesseract");
        public string Name => "local-test";
        public Task<OcrProviderResult> ExtractAsync(Stream stream, string fileName, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeRemote : IRemoteOcrProvider
    {
        public int Calls { get; private set; }
        public OcrProviderResult Result { get; set; } =
            OcrProviderResult.Success("текст из vision API");
        public string Name => "remote-test";
        public Task<OcrProviderResult> ExtractAsync(Stream stream, string fileName, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static ReceiptOcrService Service(
        IConfiguration cfg, FakeLocal local, FakeRemote remote) =>
        new(cfg, local, remote, NullLogger<ReceiptOcrService>.Instance);

    private static IConfiguration Config(string provider, string key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:Provider"] = provider,
            ["Ocr:OpenAi:ApiKey"] = key,
        }).Build();

    private static MemoryStream Image() => new(new byte[] { 1, 2, 3, 4 });

    [Fact]
    public async Task Extract_DefaultProvider_UsesLocalOnly()
    {
        var local = new FakeLocal();
        var remote = new FakeRemote();

        var result = await Service(Config("tesseract", "sk-test"), local, remote)
            .ExtractAsync(Image(), "receipt.png");

        Assert.True(result.Ok);
        Assert.Equal("текст из tesseract", result.Text);
        Assert.Equal(1, local.Calls);
        Assert.Equal(0, remote.Calls); // изображение никуда не отправлялось
    }

    [Fact]
    public async Task Extract_OpenAiExplicitlySelected_UsesRemoteOnly()
    {
        var local = new FakeLocal();
        var remote = new FakeRemote();

        var result = await Service(Config("openai", "sk-test"), local, remote)
            .ExtractAsync(Image(), "receipt.jpg");

        Assert.True(result.Ok);
        Assert.Equal("текст из vision API", result.Text);
        Assert.Equal(1, remote.Calls);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task Extract_RemoteFails_FallsBackToLocal()
    {
        var local = new FakeLocal();
        var remote = new FakeRemote
        {
            Result = OcrProviderResult.Failure("Сервис распознавания вернул 500.", 502),
        };

        var result = await Service(Config("openai", "sk-test"), local, remote)
            .ExtractAsync(Image(), "receipt.png");

        Assert.True(result.Ok);
        Assert.Equal("текст из tesseract", result.Text);
        Assert.Equal(1, remote.Calls);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task Extract_OpenAiWithoutKey_DoesNotSendImageAndUsesLocal()
    {
        var local = new FakeLocal();
        var remote = new FakeRemote();

        var result = await Service(Config("openai", ""), local, remote)
            .ExtractAsync(Image(), "receipt.png");

        Assert.True(result.Ok);
        Assert.Equal(0, remote.Calls);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task Extract_UnsupportedExtension_Returns415WithoutCallingProviders()
    {
        var local = new FakeLocal();
        var remote = new FakeRemote();

        var result = await Service(Config("openai", "sk-test"), local, remote)
            .ExtractAsync(Image(), "virus.exe");

        Assert.False(result.Ok);
        Assert.Equal(415, result.StatusCode);
        Assert.Equal(0, remote.Calls);
        Assert.Equal(0, local.Calls);
    }
}
