using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Route("api/version")]
[Authorize]
public class VersionController : ControllerBase
{
    private readonly UpdateChecker _checker;
    private readonly SelfUpdateService _selfUpdate;

    public VersionController(UpdateChecker checker, SelfUpdateService selfUpdate)
    {
        _checker = checker;
        _selfUpdate = selfUpdate;
    }

    /// <summary>Текущая версия сборки без обращения к GitHub.</summary>
    [HttpGet]
    public IActionResult Current() => Ok(AppVersion.Current);

    /// <summary>Проверка последнего коммита в GitHub-репозитории (кэш 10 минут).</summary>
    [HttpGet("check")]
    public async Task<IActionResult> Check(CancellationToken ct)
        => Ok(await _checker.CheckAsync(ct));

    // ── Автоматическое обновление по кнопке (Задача 9) ──────────────────────

    public record ApplyDto(string? Version = null, bool Restart = true);

    /// <summary>Текущее состояние самообновления и право его запускать.</summary>
    [HttpGet("apply/status")]
    public async Task<IActionResult> ApplyStatus()
    {
        var uid = UserAccess.UidOf(User);
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });

        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var canUpdate = await _selfUpdate.IsAdminAsync(uid, email);
        var state = _selfUpdate.State;
        var statusName = state.Status.ToString();

        return Ok(new
        {
            enabled = _selfUpdate.Enabled,
            canUpdate,
            status = statusName,
            targetVersion = state.TargetVersion,
            progress = state.Progress,
            error = state.Error,
            installDir = state.InstallDir,
            currentVersion = state.CurrentVersion,
            startedAt = state.StartedAt,
            finishedAt = state.FinishedAt,
            log = state.Log,
            running = state.Status is SchitayKa.Api.Services.SelfUpdateStatus.Running
                or SchitayKa.Api.Services.SelfUpdateStatus.Downloading
                or SchitayKa.Api.Services.SelfUpdateStatus.Verifying
                or SchitayKa.Api.Services.SelfUpdateStatus.Extracting
                or SchitayKa.Api.Services.SelfUpdateStatus.BackingUp
                or SchitayKa.Api.Services.SelfUpdateStatus.Applying
                or SchitayKa.Api.Services.SelfUpdateStatus.Restarting,
        });
    }

    /// <summary>Запустить обновление до указанной версии (в фоне).</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyDto? dto)
    {
        var uid = UserAccess.UidOf(User);
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (!_selfUpdate.Enabled)
            return BadRequest(new { error = "Автообновление выключено (SelfUpdate:Enabled=false)." });

        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (!await _selfUpdate.IsAdminAsync(uid, email))
            return Forbid();

        var state = await _selfUpdate.StartAsync(dto?.Version, dto?.Restart ?? true);
        return Accepted(new
        {
            status = state.Status.ToString(),
            targetVersion = state.TargetVersion,
            progress = state.Progress,
            error = state.Error,
            log = state.Log,
        });
    }

    /// <summary>Журнал обновления обычным текстом (удобно скачать и показать).</summary>
    [HttpGet("apply/log")]
    public IActionResult ApplyLog()
    {
        var uid = UserAccess.UidOf(User);
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var state = _selfUpdate.State;
        var text = string.Join("\n", state.Log);
        return Content(text, "text/plain; charset=utf-8");
    }
}
