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
    private async Task RecalcAllAccounts(int uid) => await RecalcAccountsOf(_db, _db.Accounts.Where(a => a.UserId == uid && !a.IsArchived));

    // Пересчёт для набора счетов (используется и для семейной карточки на главной)
    public static async Task RecalcAccountsOf(AppDbContext db, IQueryable<Account> accountsQ)
    {
        var ids = await accountsQ.Select(a => (int?)a.Id).ToListAsync();
        foreach (var id in ids)
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id!.Value);
            if (acc is null) continue;
            var signed = await db.Transactions.Where(t => t.AccountId == id)
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
        await db.SaveChangesAsync();
    }

    internal static object Project(Account a) => new
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
    public record ProfileDto(string Name, decimal StartingBalance);

    // Профиль: сменить имя и стартовый баланс
    [HttpPost("profile")]
    public async Task<IActionResult> UpdateProfile(ProfileDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Имя не может быть пустым" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        me.Name = dto.Name.Trim();
        me.StartingBalance = dto.StartingBalance;
        await _db.SaveChangesAsync();
        return Ok(new { me.Id, me.Email, me.Name, me.StartingBalance });
    }

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
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.Name, u.Email }).ToListAsync();
        // Создатель семьи: участник с самым ранним CreatedAt (при равных датах — меньший Id).
        var ownerId = members.Count > 0 ? members[0].Id : Uid;
        // isCreator — «я создатель этой семьи»: используется фронтендом, чтобы скрыть
        // форму «вступить по коду» для создателя (он уже в своей семье).
        var withOwner = members.Select(m => new { m.Id, m.Name, m.Email, isOwner = m.Id == ownerId, isCreator = m.Id == ownerId && m.Id == Uid }).ToList();
        return Ok(new { familyCode = me.FamilyCode, members = withOwner });
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

    // Статус семьи для карточки «Семья»: есть ли активный пригласительный код у текущего пользователя
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        var active = await _db.FamilyInvitations.FirstOrDefaultAsync(i =>
            i.OwnerUserId == Uid && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow);
        return Ok(new
        {
            familyCode = me.FamilyCode,
            code = active?.Code,
            expiresAt = active?.ExpiresAt,
        });
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

        var invite = await _db.FamilyInvitations.FirstOrDefaultAsync(i => i.Code == code);
        if (invite is null) return BadRequest(new { error = "Код не найден" });
        if (invite.OwnerUserId == Uid) return BadRequest(new { error = "Вы создали эту семью — присоединяться к себе не нужно" });
        // Пригласительный код многократный: его могут использовать несколько человек (как ссылка-приглашение).
        // Поле AcceptedAt фиксирует первое принятие (для отображения статуса), но не блокирует дальнейшие вступления.
        if (invite.ExpiresAt <= DateTime.UtcNow) return BadRequest(new { error = "Срок действия кода истёк" });

        var owner = await _db.Users.FindAsync(invite.OwnerUserId);
        if (owner?.FamilyCode is null) return BadRequest(new { error = "Семья недоступна" });

        // Если пользователь уже в семье: разрешаем только «повторное» вступление в ту же семью,
        // иначе — просим сначала выйти. Создатель своей семьи отсекается проверкой OwnerUserId выше.
        if (!string.IsNullOrEmpty(me.FamilyCode))
        {
            if (owner.FamilyCode == me.FamilyCode)
                return Ok(new { familyCode = me.FamilyCode, alreadyMember = true, members = await MembersOf(me.FamilyCode) });
            return BadRequest(new { error = "Вы уже в семье. Сначала выйдите из текущей группы." });
        }

        me.FamilyCode = owner.FamilyCode;
        if (invite.AcceptedAt is null)
        {
            invite.AcceptedAt = DateTime.UtcNow;
            invite.AcceptedByUserId = Uid;
        }
        await _db.SaveChangesAsync();

        return Ok(new { familyCode = me.FamilyCode, members = await MembersOf(me.FamilyCode) });
    }

    private async Task<List<object>> MembersOf(string familyCode) =>
        (await _db.Users.Where(u => u.FamilyCode == familyCode)
            .Select(u => new { u.Id, u.Name, u.Email }).ToListAsync())
            .Select(u => (object)new { u.Id, u.Name, u.Email }).ToList();

    // Карточка на главной: по колонке на пользователя семьи, в каждой — счета с балансами.
    [HttpGet("family-board")]
    public async Task<IActionResult> FamilyBoard()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var me = await _db.Users.FindAsync(Uid);
        if (me is null) return Unauthorized();
        var memberIds = string.IsNullOrEmpty(me.FamilyCode)
            ? new List<int> { Uid }
            : await _db.Users.Where(u => u.FamilyCode == me.FamilyCode).Select(u => u.Id).ToListAsync();

        var accounts = await _db.Accounts
            .Where(a => memberIds.Contains(a.UserId) && !a.IsArchived)
            .OrderBy(a => a.UserId).ThenBy(a => a.Type).ThenBy(a => a.Name)
            .ToListAsync();
        await AccountsController.RecalcAccountsOf(_db, _db.Accounts.Where(a => memberIds.Contains(a.UserId) && !a.IsArchived));
        accounts = await _db.Accounts.AsNoTracking()
            .Where(a => memberIds.Contains(a.UserId) && !a.IsArchived)
            .OrderBy(a => a.UserId).ThenBy(a => a.Type).ThenBy(a => a.Name)
            .ToListAsync(); // перечитать уже пересчитанные остатки

        var users = await _db.Users.Where(u => memberIds.Contains(u.Id))
            .OrderBy(u => u.Id).Select(u => new { u.Id, u.Name, u.Email, u.CreatedAt }).ToListAsync();
        // Создатель семьи — участник с самым ранним CreatedAt (при равных датах — меньший Id)
        var ownerId = users.Count > 0
            ? users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).First().Id
            : Uid;

        var columns = users.Select(u =>
        {
            var ua = accounts.Where(a => a.UserId == u.Id).ToList();
            decimal assets = ua.Where(a => a.Type != AccountType.CreditCard).Sum(a => a.Balance);
            decimal debt = ua.Where(a => a.Type == AccountType.CreditCard).Sum(a => a.Balance);
            return (object)new
            {
                userId = u.Id, name = u.Name, email = u.Email, isOwner = u.Id == ownerId, isMe = u.Id == Uid,
                totalBalance = assets - debt,
                accounts = ua.Select(a => AccountsController.Project(a)).ToList(),
            };
        }).ToList();

        return Ok(new { familyCode = me.FamilyCode, columns });
    }

    // Выйти из семьи
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
