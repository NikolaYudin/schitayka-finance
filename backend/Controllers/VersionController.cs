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

    public VersionController(UpdateChecker checker) => _checker = checker;

    /// <summary>Текущая версия сборки без обращения к GitHub.</summary>
    [HttpGet]
    public IActionResult Current() => Ok(AppVersion.Current);

    /// <summary>Проверка последнего коммита в GitHub-репозитории (кэш 10 минут).</summary>
    [HttpGet("check")]
    public async Task<IActionResult> Check(CancellationToken ct)
        => Ok(await _checker.CheckAsync(ct));
}
