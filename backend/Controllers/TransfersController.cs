using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Переводы между своими и семейными счетами (Задача 7 + 0.3/N3):
/// одно действие вместо двух операций, общий журнал переводов семьи и отмена.
/// </summary>
[ApiController]
[Route("api/transfers")]
[Authorize]
public class TransfersController : ControllerBase
{
    private readonly TransferService _transfers;

    public TransfersController(TransferService transfers) => _transfers = transfers;

    private int Uid => UserAccess.UidOf(User);

    public record TransferDto(int FromAccountId, int ToAccountId, decimal Amount, DateOnly? Date, string? Comment);

    private static object Project(Transfer t) => new
    {
        t.Id,
        t.FromAccountId,
        fromAccountName = t.FromAccount.Name,
        t.ToAccountId,
        toAccountName = t.ToAccount.Name,
        // N3: перевод виден всей семье — показываем, чьи это счета
        fromOwnerId = t.FromAccount.UserId,
        toOwnerId = t.ToAccount.UserId,
        fromOwnerName = t.FromAccount.User?.Name,
        toOwnerName = t.ToAccount.User?.Name,
        t.Amount,
        date = t.Date,
        t.Comment,
        t.CreatedAt,
        t.FromTxId,
        t.ToTxId,
        // N3, совместимость с вариантом «б»: инициатор/владелец записи перевода.
        initiatorUserId = t.UserId,
        initiatorName = t.User?.Name ?? "",
    };

    /// <summary>Переводы, видимые пользователю (свои + семейные, N3).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? month = null, [FromQuery] int? year = null,
                                          [FromQuery] int? accountId = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _transfers.ListAsync(Uid, month, year, accountId);
        return Ok(list.Select(Project));
    }

    /// <summary>Один перевод по идентификатору.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var transfer = await _transfers.GetAsync(Uid, id);
        return transfer is null ? NotFound(new { error = "Перевод не найден" }) : Ok(Project(transfer));
    }

    /// <summary>Создать перевод: две операции одним действием.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TransferDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });

        var date = dto.Date ?? DateOnly.FromDateTime(DateTime.Today);
        var result = await _transfers.CreateAsync(
            Uid, dto.FromAccountId, dto.ToAccountId, dto.Amount, date, dto.Comment);

        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(Project(result.Transfer!));
    }

    /// <summary>Отменить перевод: удаляются обе операции, остатки пересчитываются.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var (ok, error) = await _transfers.CancelAsync(Uid, id);
        if (!ok) return NotFound(new { error });
        return Ok(new { canceled = id });
    }
}
