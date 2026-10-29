using System.Security.Claims;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Route("api/accounts")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    public AccountsController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record AccountDto(string Name, AccountType Type, decimal Balance,
                             decimal? CreditLimit, decimal? CashbackRate, string? Color);

    // Полный пересчёт остатков всех активных счетов (старт + операции + кэшбэк)
    private async Task RecalcAllAccounts(int uid)
    {
        var ids = await _db.Accounts.Where(a => a.UserId == uid && !a.IsArchived)
            .Select(a => (int?)a.Id).ToListAsync();
        foreach (var id in ids)
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id!.Value && a.UserId == uid);
            if (acc is null) continue;
            var signed = await _db.Transactions.Where(t => t.AccountId == id)
                .Select(t => new { t.Type, t.Amount }).ToListAsync();
            var net = signed.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
            if (acc.Type == AccountType.CreditCard)
            {
                acc.Balance = Math.Max(0, acc.StartingBalance - net);
                acc.CashbackAccrued = 0;
            }
            else
            {
                var cashback = acc.CashbackRate > 0
                    ? Math.Round(signed.Where(t => t.Type == TxType.Expense).Sum(t => t.Amount) * acc.CashbackRate / 100m, 2)
                    : 0m;
                acc.Balance = Math.Max(0, acc.StartingBalance + net + cashback);
                acc.CashbackAccrued = cashback;
            }
        }
        await _db.SaveChangesAsync();
    }

    private static object Project(Account a) => new
    {
        a.Id, a.Name, a.Type, typeName = AccountTypeNames.Ru(a.Type),
        a.Balance, a.StartingBalance, a.CreditLimit, a.CashbackRate, a.CashbackAccrued, a.Color, a.IsArchived,
        // полезная информация для карточки счёта
        available = a.Type == AccountType.CreditCard && a.CreditLimit > 0
            ? Math.Max(0, a.CreditLimit - a.Balance) : (a.Type == AccountType.CreditCard ? 0 : a.Balance),
        creditUsagePct = a.Type == AccountType.CreditCard && a.CreditLimit > 0
            ? Math.Round(a.Balance / a.CreditLimit * 100, 1) : (decimal?)null,
    };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeArchived = false)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        await RecalcAllAccounts(Uid); // остатки всегда актуальны относительно операций
        var q = _db.Accounts.Where(a => a.UserId == Uid);
        if (!includeArchived) q = q.Where(a => !a.IsArchived);
        var list = await q.OrderBy(a => a.Type).ThenBy(a => a.Name).ToListAsync();
        return Ok(list.Select(Project));
    }

    [HttpPost]
    public async Task<IActionResult> Create(AccountDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название счёта обязательно" });
        if (dto.Balance < 0 && dto.Type != AccountType.CreditCard)
            return BadRequest(new { error = "Баланс не может быть отрицательным (для кредитной карты укажите сумму долга)" });
        var name = dto.Name.Trim();
        if (await _db.Accounts.AnyAsync(a => a.UserId == Uid && a.Name == name))
            return BadRequest(new { error = "Счёт с таким именем уже есть" });

        var acc = new Account
        {
            UserId = Uid,
            Name = name,
            Type = dto.Type,
            StartingBalance = Math.Abs(dto.Balance), // для кредитки — стартовый долг
            CreditLimit = dto.Type == AccountType.CreditCard ? (dto.CreditLimit ?? 0) : 0,
            CashbackRate = dto.CashbackRate is > 0 and <= 100 ? dto.CashbackRate.Value : 0,
            Color = string.IsNullOrWhiteSpace(dto.Color) ? DefaultColor(dto.Type) : dto.Color,
        };
        _db.Accounts.Add(acc);
        await _db.SaveChangesAsync();
        await RecalcAllAccounts(Uid);
        return Ok(Project(acc));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, AccountDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
        if (acc is null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название счёта обязательно" });
        var name = dto.Name.Trim();
        if (await _db.Accounts.AnyAsync(a => a.UserId == Uid && a.Name == name && a.Id != id))
            return BadRequest(new { error = "Счёт с таким именем уже есть" });

        acc.Name = name;
        acc.Type = dto.Type;
        acc.StartingBalance = Math.Abs(dto.Balance);
        acc.CreditLimit = dto.Type == AccountType.CreditCard ? (dto.CreditLimit ?? 0) : 0;
        acc.CashbackRate = dto.CashbackRate is > 0 and <= 100 ? dto.CashbackRate.Value : 0;
        if (!string.IsNullOrWhiteSpace(dto.Color)) acc.Color = dto.Color;
        await _db.SaveChangesAsync();
        await RecalcAllAccounts(Uid);
        return Ok(Project(acc));
    }

    // Пополнение / списание вручную: сдвиг стартового остатка (например, зачислили кэшбэк на счёт)
    public record AdjustDto(decimal Delta);
    [HttpPost("{id:int}/adjust")]
    public async Task<IActionResult> Adjust(int id, AdjustDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
        if (acc is null) return NotFound();
        acc.StartingBalance += dto.Delta;
        if (acc.StartingBalance < 0) acc.StartingBalance = 0;
        await _db.SaveChangesAsync();
        await RecalcAllAccounts(Uid);
        return Ok(Project(acc));
    }

    // Начисленный банком кэшбэк (разовое пополнение баланса)
    public record CashbackDto(decimal Amount);
    [HttpPost("{id:int}/cashback")]
    public async Task<IActionResult> Cashback(int id, CashbackDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
        if (acc is null) return NotFound();
        if (dto.Amount <= 0) return BadRequest(new { error = "Укажите сумму кэшбэка" });
        acc.StartingBalance += dto.Amount;
        await _db.SaveChangesAsync();
        await RecalcAllAccounts(Uid);
        return Ok(Project(acc));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
        if (acc is null) return NotFound();
        _db.Accounts.Remove(acc); // операции останутся, AccountId станет NULL (SetNull)
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static string DefaultColor(AccountType t) => t switch
    {
        AccountType.DebitCard => "#2b6cb0",
        AccountType.CreditCard => "#e53e3e",
        AccountType.Cash => "#38a169",
        AccountType.Deposit => "#805ad5",
        _ => "#718096",
    };
}

// ---- Семья: пригласительные коды ----
[ApiController]
[Route("api/family")]
[Authorize]
public class FamilyController : ControllerBase
{
    private readonly AppDbContext _db;
    public FamilyController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    public record JoinDto(string Code);

    private static string MakeCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // без похожих символов
        return new string(Enumerable.Range(0, 6)
            .Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
    }

    // Список членов семьи (у кого тот же FamilyCode)
    [HttpGet("members")]
    public async Task<IActionResult> Members()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        if (string.IsNullOrEmpty(me.FamilyCode)) return Ok(new { familyCode = (string?)null, members = Array.Empty<object>() });

        var members = await _db.Users.Where(u => u.FamilyCode == me.FamilyCode)
            .Select(u => new { u.Id, u.Name, u.Email }).ToListAsync();
        return Ok(new { familyCode = me.FamilyCode, members });
    }

    // Созвать семью: создаёт группу (если её нет) и выдаёт активный пригласительный код
    [HttpPost("invite")]
    public async Task<IActionResult> CreateInvite()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        if (string.IsNullOrEmpty(me.FamilyCode))
        {
            me.FamilyCode = MakeCode(); // первый член семьи становится «хозяином» группы
            await _db.SaveChangesAsync();
        }
        var active = await _db.FamilyInvitations.FirstOrDefaultAsync(i =>
            i.OwnerUserId == Uid && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow);
        if (active is null)
        {
            active = new FamilyInvitation
            {
                OwnerUserId = Uid,
                Code = MakeCode(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            };
            _db.FamilyInvitations.Add(active);
            await _db.SaveChangesAsync();
        }
        return Ok(new { code = active.Code, expiresAt = active.ExpiresAt, familyCode = me.FamilyCode });
    }

    [HttpGet("invites")]
    public async Task<IActionResult> Invites()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _db.FamilyInvitations.Where(i => i.OwnerUserId == Uid)
            .OrderByDescending(i => i.CreatedAt).Take(10)
            .Select(i => new { i.Id, i.Code, i.ExpiresAt, accepted = i.AcceptedAt != null })
            .ToListAsync();
        return Ok(list);
    }

    // Присоединиться к семье по коду приглашения
    [HttpPost("join")]
    public async Task<IActionResult> Join(JoinDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var code = dto.Code.Trim().ToUpperInvariant();
        if (code.Length == 0) return BadRequest(new { error = "Введите пригласительный код" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        if (!string.IsNullOrEmpty(me.FamilyCode))
            return BadRequest(new { error = "Вы уже в семье. Сначала выйдите из текущей группы." });

        var invite = await _db.FamilyInvitations.FirstOrDefaultAsync(i => i.Code == code && i.AcceptedAt == null);
        if (invite is null) return BadRequest(new { error = "Код не найден или уже использован" });
        if (invite.ExpiresAt <= DateTime.UtcNow) return BadRequest(new { error = "Срок действия кода истёк" });
        if (invite.OwnerUserId == Uid) return BadRequest(new { error = "Нельзя присоединиться к собственной семье" });

        var owner = await _db.Users.FindAsync(invite.OwnerUserId);
        if (owner?.FamilyCode is null) return BadRequest(new { error = "Семья недоступна" });

        me.FamilyCode = owner.FamilyCode;
        invite.AcceptedAt = DateTime.UtcNow;
        invite.AcceptedByUserId = Uid;
        await _db.SaveChangesAsync();

        var members = await _db.Users.Where(u => u.FamilyCode == me.FamilyCode)
            .Select(u => new { u.Id, u.Name, u.Email }).ToListAsync();
        return Ok(new { familyCode = me.FamilyCode, members });
    }

    // Выйти из семьи (если в группе больше одного человека — код семьи остаётся у остальных)
    [HttpPost("leave")]
    public async Task<IActionResult> Leave()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null || string.IsNullOrEmpty(me.FamilyCode)) return BadRequest(new { error = "Вы не состоите в семье" });
        var others = await _db.Users.CountAsync(u => u.FamilyCode == me.FamilyCode && u.Id != Uid);
        if (others == 0)
        {
            // последний участник — распускаем группу и её приглашения
            var invites = _db.FamilyInvitations.Where(i => i.Code == me.FamilyCode);
            _db.FamilyInvitations.RemoveRange(invites);
        }
        me.FamilyCode = null;
        await _db.SaveChangesAsync();
        return Ok(new { left = true });
    }
}
