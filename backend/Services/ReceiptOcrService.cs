using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SchitayKa.Api.Services;

public sealed record OcrResult(bool Ok, string? Text, string? Error, int StatusCode);

/// <summary>
/// OCR через локальный tesseract CLI. В Docker-образе tesseract-ocr + rus/eng
/// устанавливаются заранее; при отсутствии бинарника возвращается понятная ошибка,
/// а пользователь может разобрать текст вручную.
/// </summary>
public sealed class ReceiptOcrService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff",
    };

    private readonly IConfiguration _cfg;

    public ReceiptOcrService(IConfiguration cfg) => _cfg = cfg;

    public async Task<OcrResult> ExtractAsync(
        Stream stream, string fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (!AllowedExtensions.Contains(ext))
            return new OcrResult(false, null,
                "Поддерживаются изображения JPG, PNG, WebP, BMP или TIFF.", 415);

        var tempFile = Path.Combine(Path.GetTempPath(), $"schitayka-ocr-{Guid.NewGuid():N}{ext.ToLowerInvariant()}");
        try
        {
            await using (var file = File.Create(tempFile))
                await stream.CopyToAsync(file, ct);

            var tesseract = _cfg["Ocr:TesseractPath"];
            if (string.IsNullOrWhiteSpace(tesseract)) tesseract = "tesseract";
            var languages = _cfg["Ocr:Languages"];
            if (string.IsNullOrWhiteSpace(languages)) languages = "rus+eng";
            var timeoutSeconds = _cfg.GetValue<int?>("Ocr:TimeoutSeconds") ?? 30;

            var psi = new ProcessStartInfo
            {
                FileName = tesseract,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add("stdout");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(languages);
            psi.ArgumentList.Add("--psm");
            psi.ArgumentList.Add("6");

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Win32Exception)
            {
                return new OcrResult(false, null,
                    "OCR не настроен на сервере: не найден tesseract. Можно вставить текст чека вручную.", 503);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120)));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return new OcrResult(false, null,
                    "Распознавание заняло слишком много времени. Попробуйте другой скриншот или вставьте текст вручную.", 504);
            }

            var output = await stdoutTask;
            var error = await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var message = string.IsNullOrWhiteSpace(error)
                    ? "Не удалось распознать текст на изображении."
                    : $"Не удалось распознать текст: {Trim(error)}";
                return new OcrResult(false, null, message, 422);
            }

            return new OcrResult(true, output, null, 200);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    private static string Trim(string text)
    {
        var line = text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 240 ? line[..240] + "…" : line;
    }
}
