using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;

namespace SchitayKa.Api.Services;

/// <summary>
/// Персистентный аудит чувствительных действий (B10).
/// В Details пишется только короткая метаинформация: имя файла, размер, результат —
/// никогда пароли, токены, текст чеков или изображения.
/// </summary>
public sealed class AuditService
{
    public const string ExportProfile = "export.profile";
    public const string ImportReceipt = "import.receipt";
    public const string ImportReceiptText = "import.receipt.text";
    public const string ImportReceiptQr = "import.receipt.qr";
    public const string PasswordChanged = "auth.password-changed";
    public const string PasswordFailed = "auth.password-failed";

    private readonly AppDbContext _db;
    public AuditService(AppDbContext db) => _db = db;

    public async Task LogAsync(
        int userId, string action, string details, HttpContext? context = null,
        CancellationToken ct = default)
    {
        if (userId <= 0) return;
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Details = Truncate(details, 220),
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(context?.Request.Headers.UserAgent.ToString(), 180),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
