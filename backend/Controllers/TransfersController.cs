using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Переводы между своими счетами (Задача 7):
/// одно действие вместо двух операций, журнал переводов и отмена.
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
        t.Amount,
        date = t.Date,
        t.Comment,
        t.CreatedAt,
        t.FromTxId,
        t.ToTxId,
    };

    /// <summary>Переводы пользователя (по месяцу/году и/или счёту).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? month = null, [FromQuery] int? year = null,
                                          [FromQuery] int? accountId = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _transfers.ListAsync(Uid, month, year, accountId);
        return Ok(list.Select(Project));
    }

    /// <summary>Создать перевод: две операции одним действием.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var transfer = (await _transfers.ListAsync(Uid)).FirstOrDefault(t => t.Id == id);
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
