namespace SchitayKa.Api.Services;

/// <summary>Публичный контракт результата OCR (используется контроллером и UI).</summary>
public sealed record OcrResult(bool Ok, string? Text, string? Error, int StatusCode);

/// <summary>
/// Фасад распознавания чеков. Выбирает провайдера по настройке <c>Ocr:Provider</c>:
/// по умолчанию работает только локальный tesseract. Удалённый OpenAI-провайдер
/// вызывается, только если провайдер явно выбран И задан ключ; при любой ошибке
/// изображение повторно подаётся локальному провайдеру (fallback).
/// </summary>
public sealed class ReceiptOcrService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff",
    };

    private readonly IConfiguration _cfg;
    private readonly ILocalOcrProvider _local;
    private readonly IRemoteOcrProvider _remote;
    private readonly ILogger<ReceiptOcrService> _log;

    public ReceiptOcrService(
        IConfiguration cfg,
        ILocalOcrProvider local,
        IRemoteOcrProvider remote,
        ILogger<ReceiptOcrService> log)
    {
        _cfg = cfg;
        _local = local;
        _remote = remote;
        _log = log;
    }

    /// <summary>Имя провайдера, который будет вызван первым (для диагностики).</summary>
    public string ActiveProviderName => UseRemote ? _remote.Name : _local.Name;

    private bool UseRemote =>
        string.Equals(_cfg["Ocr:Provider"], "openai", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_cfg["Ocr:OpenAi:ApiKey"]);

    public async Task<OcrResult> ExtractAsync(
        Stream stream, string? fileName, CancellationToken ct = default)
    {
        var safeName = fileName ?? "";
        var ext = Path.GetExtension(safeName);
        if (!AllowedExtensions.Contains(ext))
            return new OcrResult(false, null,
                "Поддерживаются изображения JPG, PNG, WebP, BMP или TIFF.", 415);

        if (!UseRemote)
            return ToResult(await _local.ExtractAsync(stream, safeName, ct));

        // Удалённый провайдер может не сработать — буферизуем изображение,
        // чтобы при fallback отдать те же байты локальному tesseract.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var remote = await _remote.ExtractAsync(buffer, safeName, ct);
        if (remote.Ok)
            return ToResult(remote);

        _log.LogWarning(
            "OCR-провайдер {Remote} не сработал ({Error}). Переход на локальный {Local}.",
            _remote.Name, remote.Error, _local.Name);
        buffer.Position = 0;
        var local = await _local.ExtractAsync(buffer, safeName, ct);
        if (!local.Ok && !string.IsNullOrWhiteSpace(remote.Error))
            return new OcrResult(false, null,
                $"{local.Error} ({_remote.Name}: {remote.Error})", local.StatusCode);

        return ToResult(local);
    }

    private static OcrResult ToResult(OcrProviderResult r) => new(r.Ok, r.Text, r.Error, r.StatusCode);
}
