using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Route("api/import")]
[Authorize]
public class ImportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ReceiptOcrService _ocr;
    private readonly AuditService _audit;

    public ImportController(AppDbContext db, ReceiptOcrService ocr, AuditService audit)
    {
        _db = db;
        _ocr = ocr;
        _audit = audit;
    }

    private int Uid => UserAccess.UidOf(User);

    /// <summary>Распознать фото чека или скриншот банка.</summary>
    [HttpPost("receipt")]
    [EnableRateLimiting("import")]
    [RequestSizeLimit(12_000_000)]
    public async Task<IActionResult> Receipt([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Выберите изображение чека или скриншот." });
        if (file.Length > 10_000_000)
            return BadRequest(new { error = "Файл больше 10 МБ. Сожмите изображение и попробуйте снова." });

        var contentType = file.ContentType ?? "";
        var ext = Path.GetExtension(file.FileName);
        var allowedExt = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff" };
        // Проверяем оба признака: и Content-Type, и расширение. Раньше условия были
        // объединены через «ИЛИ», поэтому произвольные байты уходили в tesseract/libtiff.
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || !allowedExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Нужно изображение JPG, PNG, WebP, BMP или TIFF." });
        }

        await using var stream = file.OpenReadStream();
        var ocr = await _ocr.ExtractAsync(stream, file.FileName, ct);
        if (!ocr.Ok)
        {
            await _audit.LogAsync(Uid, AuditService.ImportReceipt,
                $"файл {Path.GetFileName(file.FileName)}, {file.Length} байт, OCR ошибка {ocr.StatusCode}",
                HttpContext, ct);
            return StatusCode(ocr.StatusCode, new { error = ocr.Error });
        }

        await _audit.LogAsync(Uid, AuditService.ImportReceipt,
            $"файл {Path.GetFileName(file.FileName)}, {file.Length} байт, OCR успешно", HttpContext, ct);
        return Ok(await ParseTextAsync(ocr.Text ?? ""));
    }

    public record TextDto(string Text);

    /// <summary>Разобрать текст, вставленный вручную (резервный путь, если OCR недоступен).</summary>
    [HttpPost("receipt/text")]
    [EnableRateLimiting("import")]
    public async Task<IActionResult> ReceiptText([FromBody] TextDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var text = dto.Text?.Trim() ?? "";
        if (text.Length < 5)
            return BadRequest(new { error = "Вставьте текст чека (минимум 5 символов)." });
        if (text.Length > 20_000)
            return BadRequest(new { error = "Слишком длинный текст (максимум 20 000 символов)." });

        await _audit.LogAsync(Uid, AuditService.ImportReceiptText,
            $"вставленный текст, {text.Length} символов", HttpContext);
        return Ok(await ParseTextAsync(text));
    }

    /// <summary>
    /// Разобрать QR-код чека ФНС локально, без обращения к ФНС (B1).
    /// Поддерживается строка вида «t=…&amp;s=…&amp;fn=…&amp;i=…&amp;fp=…&amp;n=…».
    /// </summary>
    [HttpPost("receipt/qr")]
    [EnableRateLimiting("import")]
    public async Task<IActionResult> ReceiptQr([FromBody] TextDto dto, CancellationToken ct)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var qr = ReceiptQrParser.Parse(dto.Text);
        if (!qr.Ok || qr.Data is null)
            return BadRequest(new { error = qr.Error ?? "Не удалось разобрать QR-код чека." });

        await _audit.LogAsync(Uid, AuditService.ImportReceiptQr,
            $"QR {dto.Text?.Length ?? 0} символов, ФД {qr.Data.FiscalDocument}", HttpContext, ct);
        return Ok(ReceiptParser.FromQr(qr.Data, dto.Text ?? ""));
    }

    private async Task<ReceiptParseResult> ParseTextAsync(string text)
    {
        var categories = await (await CategoryService.AccessibleAsync(_db, Uid))
            .OrderBy(c => c.Name)
            .Select(c => new CategoryRef(c.Id, c.Name, c.Type))
            .ToListAsync();

        return ReceiptParser.Parse(text, categories);
    }
}
