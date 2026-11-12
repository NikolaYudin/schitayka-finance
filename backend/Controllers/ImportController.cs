using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public ImportController(AppDbContext db, ReceiptOcrService ocr)
    {
        _db = db;
        _ocr = ocr;
    }

    private int Uid => UserAccess.UidOf(User);

    /// <summary>Распознать фото чека или скриншот банка.</summary>
    [HttpPost("receipt")]
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
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !allowedExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Нужно изображение JPG, PNG, WebP, BMP или TIFF." });
        }

        await using var stream = file.OpenReadStream();
        var ocr = await _ocr.ExtractAsync(stream, file.FileName, ct);
        if (!ocr.Ok)
            return StatusCode(ocr.StatusCode, new { error = ocr.Error });

        return Ok(await ParseTextAsync(ocr.Text ?? ""));
    }

    public record TextDto(string Text);

    /// <summary>Разобрать текст, вставленный вручную (резервный путь, если OCR недоступен).</summary>
    [HttpPost("receipt/text")]
    public async Task<IActionResult> ReceiptText([FromBody] TextDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var text = dto.Text?.Trim() ?? "";
        if (text.Length < 5)
            return BadRequest(new { error = "Вставьте текст чека (минимум 5 символов)." });
        if (text.Length > 20_000)
            return BadRequest(new { error = "Слишком длинный текст (максимум 20 000 символов)." });

        return Ok(await ParseTextAsync(text));
    }

    private async Task<ReceiptParseResult> ParseTextAsync(string text)
    {
        var categories = await _db.Categories
            .Where(c => c.UserId == Uid)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryRef(c.Id, c.Name, c.Type))
            .ToListAsync();

        return ReceiptParser.Parse(text, categories);
    }
}
