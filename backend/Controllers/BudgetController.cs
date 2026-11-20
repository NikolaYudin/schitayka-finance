using System.Security.Claims;
using SchitayKa.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Authorize] // без валидного JWT запрос не дойдёт до контроллера — вернёт 401, а не 500
[Route("api/budget")]
public class BudgetController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SchitayKa.Api.Services.AccountRulesService _rules;
    private readonly BudgetSummaryService _summary;

    // Конструктор для существующих тестов: сервис сводки создаётся по умолчанию.
    public BudgetController(AppDbContext db, SchitayKa.Api.Services.AccountRulesService rules)
        : this(db, rules, new BudgetSummaryService(db, rules)) { }

    // Конструктор для DI: явно помечаем его, иначе ActivatorUtilities видит TWO
    // подходящих конструктора (2-аргументный для тестов тоже строится из сервисов)
    // и падает с «Multiple constructors accepting all given argument types».
    [ActivatorUtilitiesConstructor]
    public BudgetController(AppDbContext db, SchitayKa.Api.Services.AccountRulesService rules,
        BudgetSummaryService summary)
    { _db = db; _rules = rules; _summary = summary; }

    private int CurrentUserId => UserAccess.UidOf(User);

    public record StartBalanceDto(decimal StartingBalance);
    public record TxCreateDto(int CategoryId, TxType Type, decimal Amount, DateOnly? Date, string? Comment, int? AccountId, int? ForUserId);

    // Создать операцию для члена семьи (доступно только владельцу семейной группы)
    [HttpPost("tx-for")]
    public async Task<IActionResult> CreateTxForFamily(TxCreateDto dto)
    {
        var uid = CurrentUserId;
        if (dto.Amount <= 0) return BadRequest(new { error = "Сумма должна быть больше нуля" });
        var me = await _db.Users.FindAsync(uid);
        if (me is null || string.IsNullOrEmpty(me.FamilyCode))
            return BadRequest(new { error = "Вы не в семье" });
        // Владелец группы: тот, кто создавал приглашения (есть FamilyInvitations с OwnerUserId) либо первый участник
        var isOwner = await _db.FamilyInvitations.AnyAsync(i => i.OwnerUserId == uid && i.AcceptedAt != null);
        if (!isOwner)
        {
            var firstMemberId = await _db.Users.Where(u => u.FamilyCode == me.FamilyCode).OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
            isOwner = firstMemberId == uid;
        }
        if (!isOwner) return Forbid();
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == dto.ForUserId && u.FamilyCode == me.FamilyCode);
        if (target is null) return BadRequest(new { error = "Пользователь не найден в вашей семье" });

        // B13: правила действуют и на операции, созданные «за члена семьи» —
        // используем правила и категории владельца операции.
        var match = await _rules.MatchAssignmentAsync(target.Id,
            new AccountRulesService.RuleCandidate(dto.Type, dto.Amount, dto.Comment, dto.CategoryId));
        var effectiveCategoryId = dto.CategoryId;
        if (match?.SetCategoryId is { } setCat
            && await CategoryService.FindAsync(_db, target.Id, setCat) is not null)
            effectiveCategoryId = setCat;
        var cat = await CategoryService.FindAsync(_db, target.Id, effectiveCategoryId);
        if (cat is null) return BadRequest(new { error = "Категория не найдена" });
        if (cat.Type != dto.Type) return BadRequest(new { error = "Тип операции не совпадает с категорией" });

        int? accId = dto.AccountId ?? match?.AccountId
            ?? (await _db.Accounts.FirstOrDefaultAsync(a => a.UserId == target.Id && !a.IsArchived))?.Id;
        if (accId.HasValue)
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accId.Value && a.UserId == target.Id && !a.IsArchived);
            if (acc is null) return BadRequest(new { error = "Счёт не найден у этого пользователя" });
        }
        var tx = new Transaction
        {
            UserId = target.Id,
            CategoryId = effectiveCategoryId,
            Type = dto.Type,
            Amount = dto.Amount,
            Date = dto.Date ?? DateOnly.FromDateTime(DateTime.Today),
            Comment = dto.Comment?.Trim() ?? "",
            AccountId = accId,
        };
        _db.Transactions.Add(tx);

        // Операция и пересчёт баланса счёта получателя должны применяться вместе:
        // иначе при ошибке клиент получит 500, а операция останется в базе.
        await using var tr = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        if (tx.AccountId is { } txAccId)
        {
            await AccountsController.RecalcAccountsOf(_db, _db.Accounts.Where(a => a.Id == txAccId));
            await _rules.EvaluateAllAsync(target.Id);
        }
        await tr.CommitAsync();

        return Ok(new { tx.Id });
    }

    // Установить стартовый баланс («сколько денег на счетах сейчас»)
    [HttpPost("start-balance")]
    public async Task<IActionResult> SetStartBalance(StartBalanceDto dto)
    {
        var uid = CurrentUserId;
        var user = await _db.Users.FindAsync(uid);
        if (user is null) return Unauthorized();
        user.StartingBalance = dto.StartingBalance;
        await _db.SaveChangesAsync();
        return Ok(new { user.StartingBalance });
    }

    // Сводка личного бюджета + персональные рекомендации.
    // 0.3/N4: параметр охвата — me (по умолчанию, свои данные), family (все члены семьи)
    // или ids (выбранные члены через параметр ids=1,2,3; проверяются по FamilyAccess).
    // Личные инструменты (лимиты, цели, уведомления, резервы) всегда считаются по текущему
    // пользователю — у семьи они свои у каждого.
    // Вся логика агрегации вынесена в BudgetSummaryService; здесь только авторизация и ответ.
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int year = 0, [FromQuery] int month = 0,
        [FromQuery] string? scope = null, [FromQuery] string? ids = null)
    {
        var uid = CurrentUserId;
        if (uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (year <= 0 || month <= 0) { year = DateTime.Today.Year; month = DateTime.Today.Month; }

        var result = await _summary.GetAsync(uid, year, month, scope, ids);
        return Ok(result);
    }
}
