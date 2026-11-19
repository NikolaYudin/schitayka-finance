using System.Net;
using System.Text.Json;

namespace SchitayKa.Api.Services;

/// <summary>
/// OCR через OpenAI-совместимый vision API (Chat Completions).
/// ВАЖНО: изображение отправляется вовне только тогда, когда администратор явно
/// включил <c>Ocr:Provider=openai</c> и указал ключ. При любом сбое фасад
/// переключается на локальный tesseract (см. <see cref="ReceiptOcrService"/>).
/// </summary>
public sealed class OpenAiVisionOcrProvider : IRemoteOcrProvider
{
    public const string SourceName = "openai-vision";
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OpenAiVisionOcrProvider> _log;

    public OpenAiVisionOcrProvider(
        IHttpClientFactory http, IConfiguration cfg, ILogger<OpenAiVisionOcrProvider> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Name => SourceName;

    public async Task<OcrProviderResult> ExtractAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        var apiKey = _cfg["Ocr:OpenAi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return OcrProviderResult.Failure("Не задан ключ OpenAI (Ocr:OpenAi:ApiKey).", 503);

        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return OcrProviderResult.Failure("Не удалось прочитать изображение.", 422);
        }
        if (bytes.Length == 0)
            return OcrProviderResult.Failure("Изображение пустое.", 422);

        var endpoint = _cfg["Ocr:OpenAi:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = DefaultEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return OcrProviderResult.Failure("Некорректный адрес OpenAI-совместимого API.", 503);

        var model = _cfg["Ocr:OpenAi:Model"];
        if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;

        var dataUrl = $"data:{MimeOf(fileName)};base64,{Convert.ToBase64String(bytes)}";
        var payload = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = "Распознай текст чека на изображении. Верни только текст как есть, без пояснений и без markdown.",
                        },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
            temperature = 0,
            max_tokens = 2048,
        };

        try
        {
            var client = _http.CreateClient("ocr-openai");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? 503 : 502;
                _log.LogWarning("OpenAI vision: HTTP {(int)StatusCode} при распознавании чека", response.StatusCode);
                return OcrProviderResult.Failure(
                    $"Сервис распознавания вернул {(int)response.StatusCode}.", status);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var text = ExtractContent(json);
            if (string.IsNullOrWhiteSpace(text))
                return OcrProviderResult.Failure("Сервис распознавания вернул пустой текст.", 422);

            return OcrProviderResult.Success(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OcrProviderResult.Failure("Сервис распознавания не ответил вовремя.", 504);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "OpenAI vision: не удалось распознать чек");
            return OcrProviderResult.Failure("Сервис распознавания временно недоступен.", 502);
        }
    }

    /// <summary>Достаёт текст ответа из choices[0].message.content.</summary>
    internal static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content) ? content.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string MimeOf(string fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/jpeg",
    };
}
