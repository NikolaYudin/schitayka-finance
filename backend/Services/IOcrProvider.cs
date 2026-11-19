namespace SchitayKa.Api.Services;

/// <summary>
/// Результат распознавания изображения одним OCR-провайдером.
/// По смыслу совпадает с <see cref="OcrResult"/>, но используется внутри провайдеров,
/// чтобы фасад мог организовать fallback, не меняя публичный JSON-контракт.
/// </summary>
public sealed record OcrProviderResult(bool Ok, string? Text, string? Error, int StatusCode)
{
    public static OcrProviderResult Success(string text) => new(true, text, null, 200);
    public static OcrProviderResult Failure(string error, int statusCode) => new(false, null, error, statusCode);
}

/// <summary>
/// Провайдер распознавания текста чека с изображения.
/// Построен по образцу <see cref="IQuoteProvider"/>: провайдер ничего не знает
/// о контроллерах и базе данных, а фасад выбирает, какой из них вызвать.
/// </summary>
public interface IOcrProvider
{
    /// <summary>Короткое имя источника (для логов и аудита).</summary>
    string Name { get; }

    /// <summary>Распознать изображение и вернуть текст или понятную ошибку.</summary>
    Task<OcrProviderResult> ExtractAsync(
        Stream stream, string fileName, CancellationToken ct = default);
}

/// <summary>Локальный провайдер (tesseract CLI) — применяется по умолчанию.</summary>
public interface ILocalOcrProvider : IOcrProvider { }

/// <summary>
/// Удалённый провайдер — вызывается только при явном включении в настройках
/// <c>Ocr:Provider=openai</c> и наличии ключа.
/// </summary>
public interface IRemoteOcrProvider : IOcrProvider { }
