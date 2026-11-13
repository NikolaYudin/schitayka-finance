using SchitayKa.Api.Data;
using SchitayKa.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SchitayKa.Api.Controllers;

[ApiController]
[Route("api/account-rules")]
[Authorize]
public class AccountRulesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AccountRulesService _rules;
    public AccountRulesController(AppDbContext db, AccountRulesService rules) { _db = db; _rules = rules; }
    private int Uid => UserAccess.UidOf(User);

    public record RuleDto(
        string Name,
        AccountRuleKind Kind,
        bool IsEnabled = true,
        int Priority = 100,
        int? AccountId = null,
        TxType? TxType = null,
        int? MatchCategoryId = null,
        string? CommentContains = null,
        decimal? AmountMin = null,
        decimal? AmountMax = null,
        int? SetCategoryId = null,
        decimal? Threshold = null,
        int? DefaultReturnDays = null,
        bool NotifyInApp = true);

    public record CandidateDto(TxType Type, decimal Amount, string? Comment, int? CategoryId, int? AccountId = null);

    private async Task<object> ProjectAsync(AccountRule r)
    {
        var account = r.AccountId is { } aid
            ? await _db.Accounts.Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefaultAsync()
            : null;
        var matchCat = r.MatchCategoryId is { } mid
            ? await _db.Categories.Where(c => c.Id == mid).Select(c => c.Name).FirstOrDefaultAsync()
            : null;
        var setCat = r.SetCategoryId is { } sid
            ? await _db.Categories.Where(c => c.Id == sid).Select(c => c.Name).FirstOrDefaultAsync()
            : null;

        return new
        {
            r.Id, r.Name, r.Kind,
            kindName = r.Kind switch
            {
                AccountRuleKind.AutoAssign => "Автоподстановка",
                AccountRuleKind.MinBalance => "Неснижаемый остаток",
                _ => "Резерв",
            },
            r.IsEnabled, r.Priority, r.AccountId, accountName = account,
            r.TxType, r.MatchCategoryId, matchCategoryName = matchCat,
            r.CommentContains, r.AmountMin, r.AmountMax,
            r.SetCategoryId, setCategoryName = setCat,
            r.Threshold, r.DefaultReturnDays, r.NotifyInApp, r.CreatedAt,
        };
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AccountRuleKind? kind = null)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var q = _db.AccountRules.Where(r => r.UserId == Uid);
        if (kind is { } k) q = q.Where(r => r.Kind == k);
        var rules = await q.OrderBy(r => r.Kind).ThenBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync();
        var list = new List<object>();
        foreach (var r in rules) list.Add(await ProjectAsync(r));
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create(RuleDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var error = await ValidateAsync(dto);
        if (error is not null) return BadRequest(new { error });

        var rule = new AccountRule();
        ApplyDto(rule, dto);
        rule.UserId = Uid;
        _db.AccountRules.Add(rule);
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok(await ProjectAsync(rule));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, RuleDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var rule = await _db.AccountRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == Uid);
        if (rule is null) return NotFound();
        var error = await ValidateAsync(dto, id);
        if (error is not null) return BadRequest(new { error });

        ApplyDto(rule, dto);
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok(await ProjectAsync(rule));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var rule = await _db.AccountRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == Uid);
        if (rule is null) return NotFound();
        _db.AccountRules.Remove(rule);
        await _db.SaveChangesAsync();
        await _rules.EvaluateAllAsync(Uid);
        return Ok();
    }

    // Проверить правило на виртуальной операции без сохранения.
    [HttpPost("test")]
    public async Task<IActionResult> Test(CandidateDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var match = await _rules.MatchAssignmentAsync(Uid,
            new AccountRulesService.RuleCandidate(dto.Type, dto.Amount, dto.Comment, dto.CategoryId));
        if (match is null) return Ok(new { matched = false });
        return Ok(new
        {
            matched = true,
            ruleId = match.RuleId, ruleName = match.RuleName,
            accountId = match.AccountId, categoryId = match.SetCategoryId,
        });
    }

    // Применить автоправила к существующим операциям (по умолчанию — только без счёта).
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromQuery] bool dryRun = true, [FromQuery] bool overwrite = false)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });

        var rules = await _db.AccountRules
            .Where(r => r.UserId == Uid && r.Kind == AccountRuleKind.AutoAssign && r.IsEnabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync();
        if (rules.Count == 0) return Ok(new { dryRun, matched = 0, samples = Array.Empty<object>() });

        var activeAccountIds = (await _db.Accounts
            .Where(a => a.UserId == Uid && !a.IsArchived)
            .Select(a => a.Id).ToListAsync()).ToHashSet();
        var validCategoryIds = (await _db.Categories
            .Where(c => c.UserId == Uid).Select(c => c.Id).ToListAsync()).ToHashSet();

        var txQuery = _db.Transactions.Where(t => t.UserId == Uid);
        if (!overwrite) txQuery = txQuery.Where(t => t.AccountId == null);
        var txs = await txQuery.OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();

        var matched = 0;
        var samples = new List<object>();
        foreach (var tx in txs)
        {
            var result = AccountRulesService.MatchRule(rules, activeAccountIds,
                new AccountRulesService.RuleCandidate(tx.Type, tx.Amount, tx.Comment, tx.CategoryId));
            if (result is null) continue;
            matched++;

            if (samples.Count < 20)
                samples.Add(new { tx.Id, tx.Date, tx.Comment, ruleName = result.RuleName,
                    accountId = result.AccountId, categoryId = result.SetCategoryId });

            if (dryRun) continue;
            if (result.AccountId is { } newAccount) tx.AccountId = newAccount;
            if (result.SetCategoryId is { } newCategory && validCategoryIds.Contains(newCategory))
                tx.CategoryId = newCategory;
        }

        if (!dryRun && matched > 0)
        {
            await _db.SaveChangesAsync();
            await RecalcAllAccountsAsync(Uid);
            await _rules.EvaluateAllAsync(Uid);
        }

        return Ok(new { dryRun, matched, samples });
    }

    private async Task<string?> ValidateAsync(RuleDto dto, int? ruleId = null)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "Название правила обязательно";
        if (dto.Priority < 0 || dto.Priority > 10_000) return "Приоритет должен быть от 0 до 10000";

        if (dto.AccountId is { } accountId)
        {
            var account = await _db.Accounts.FirstOrDefaultAsync(a =>
                a.Id == accountId && a.UserId == Uid && !a.IsArchived);
            if (account is null) return "Счёт не найден или находится в архиве";
            if (dto.Kind == AccountRuleKind.EmergencyReserve && account.Type == AccountType.CreditCard)
                return "Резерв можно задать только для некредитного счёта";
        }

        if (dto.Kind is (AccountRuleKind.AutoAssign or AccountRuleKind.MinBalance) && dto.AccountId is null)
            return "Для этого типа правила выберите счёт";

        if (dto.Kind is (AccountRuleKind.MinBalance or AccountRuleKind.EmergencyReserve)
            && dto.Threshold is null)
            return "Укажите сумму порога";

        if (dto.Threshold is < 0) return "Порог не может быть отрицательным";
        if (dto.DefaultReturnDays is < 1 or > 365) return "Срок возврата — от 1 до 365 дней";

        if (dto.AmountMin is < 0 || dto.AmountMax is < 0) return "Суммы условия не могут быть отрицательными";
        if (dto.AmountMin is { } min && dto.AmountMax is { } max && min > max)
            return "Минимальная сумма не может быть больше максимальной";

        if (dto.MatchCategoryId is { } matchId
            && !await _db.Categories.AnyAsync(c => c.Id == matchId && c.UserId == Uid))
            return "Категория условия не найдена";
        if (dto.SetCategoryId is { } setId
            && !await _db.Categories.AnyAsync(c => c.Id == setId && c.UserId == Uid))
            return "Категория для подстановки не найдена";

        // Счёт для MinBalance должен быть активным; для AutoAssign — тоже.
        if (dto.Kind != AccountRuleKind.EmergencyReserve && dto.AccountId is null)
            return "Выберите счёт";

        _ = ruleId; // зарезервировано для будущих проверок уникальности
        return null;
    }

    private static void ApplyDto(AccountRule rule, RuleDto dto)
    {
        rule.Name = dto.Name.Trim();
        rule.Kind = dto.Kind;
        rule.IsEnabled = dto.IsEnabled;
        rule.Priority = dto.Priority;
        rule.AccountId = dto.AccountId;
        rule.TxType = dto.Kind == AccountRuleKind.AutoAssign ? dto.TxType : null;
        rule.MatchCategoryId = dto.Kind == AccountRuleKind.AutoAssign ? dto.MatchCategoryId : null;
        rule.CommentContains = dto.Kind == AccountRuleKind.AutoAssign
            ? (string.IsNullOrWhiteSpace(dto.CommentContains) ? null : dto.CommentContains.Trim())
            : null;
        rule.AmountMin = dto.Kind == AccountRuleKind.AutoAssign ? dto.AmountMin : null;
        rule.AmountMax = dto.Kind == AccountRuleKind.AutoAssign ? dto.AmountMax : null;
        rule.SetCategoryId = dto.Kind == AccountRuleKind.AutoAssign ? dto.SetCategoryId : null;
        rule.Threshold = dto.Kind is AccountRuleKind.MinBalance or AccountRuleKind.EmergencyReserve
            ? dto.Threshold : null;
        rule.DefaultReturnDays = dto.Kind == AccountRuleKind.EmergencyReserve ? dto.DefaultReturnDays : null;
        rule.NotifyInApp = dto.NotifyInApp;
    }

    // Локальный пересчёт балансов: bulk-apply меняет привязку операций к счетам.
    private async Task RecalcAllAccountsAsync(int uid)
    {
        var accounts = await _db.Accounts.Where(a => a.UserId == uid).ToListAsync();
        foreach (var acc in accounts)
        {
            var signed = await _db.Transactions.Where(t => t.AccountId == acc.Id)
                .Select(t => new { t.Type, t.Amount, t.IsTransfer }).ToListAsync();
            var net = signed.Sum(t => t.Type == TxType.Income ? t.Amount : -t.Amount);
            if (acc.Type == AccountType.CreditCard)
            {
                acc.Balance = Math.Max(0, acc.StartingBalance - net);
                acc.CashbackAccrued = 0;
            }
            else
            {
                var cashback = acc.CashbackRate > 0
                    ? Math.Round(signed.Where(t => t.Type == TxType.Expense && !t.IsTransfer).Sum(t => t.Amount) * acc.CashbackRate / 100m, 2)
                    : 0m;
                acc.Balance = Math.Max(0, acc.StartingBalance + net + cashback);
                acc.CashbackAccrued = cashback;
            }
        }
        await _db.SaveChangesAsync();
    }
}

[ApiController]
[Route("api/alerts")]
[Authorize]
public class AlertsController : ControllerBase
{
    private readonly AppDbContext _db;
    public AlertsController(AppDbContext db) { _db = db; }
    private int Uid => UserAccess.UidOf(User);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var list = await _db.AccountAlerts
            .Where(a => a.UserId == Uid && a.ResolvedAt == null && a.DismissedAt == null)
            .OrderBy(a => a.Level == "danger" ? 0 : 1).ThenByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.AccountId, a.RuleId, a.Kind, a.Level, a.Message,
                a.BalanceSnapshot, a.CreatedAt,
                accountName = a.AccountId == null ? null
                    : _db.Accounts.Where(x => x.Id == a.AccountId).Select(x => x.Name).FirstOrDefault(),
                ruleName = a.RuleId == null ? null
                    : _db.AccountRules.Where(x => x.Id == a.RuleId).Select(x => x.Name).FirstOrDefault(),
            })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost("{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var alert = await _db.AccountAlerts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid);
        if (alert is null) return NotFound();
        alert.DismissedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { dismissed = true });
    }
}

[ApiController]
[Route("api/reserve-loans")]
[Authorize]
public class ReserveLoansController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AccountRulesService _rules;
    public ReserveLoansController(AppDbContext db, AccountRulesService rules) { _db = db; _rules = rules; }
    private int Uid => UserAccess.UidOf(User);

    public record LoanDto(int AccountId, decimal Amount, int? RuleId = null,
                          DateTime? DueDate = null, string? Comment = null, int? TransactionId = null);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? accountId = null, [FromQuery] bool activeOnly = true)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var q = _db.ReserveLoans.Where(l => l.UserId == Uid);
        if (accountId is { } aid) q = q.Where(l => l.AccountId == aid);
        if (activeOnly) q = q.Where(l => l.ReturnedAt == null);
        var today = DateTime.UtcNow.Date;
        var list = await q.OrderBy(l => l.ReturnedAt != null).ThenBy(l => l.DueDate).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.AccountId, l.RuleId, l.Amount, l.TakenAt, l.DueDate, l.ReturnedAt, l.TransactionId, l.Comment,
                accountName = _db.Accounts.Where(a => a.Id == l.AccountId).Select(a => a.Name).FirstOrDefault(),
                isOverdue = l.ReturnedAt == null && l.DueDate < today,
            })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create(LoanDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        try
        {
            var loan = await _rules.TakeReserveLoanAsync(Uid, dto.RuleId, dto.AccountId, dto.Amount,
                dto.DueDate ?? default, dto.TransactionId, dto.Comment ?? "");
            var accountName = await _db.Accounts.Where(a => a.Id == loan.AccountId)
                .Select(a => a.Name).FirstOrDefaultAsync();
            return Ok(new { loan.Id, loan.AccountId, accountName, loan.RuleId, loan.Amount,
                loan.TakenAt, loan.DueDate, loan.TransactionId, loan.Comment });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/return")]
    public async Task<IActionResult> Return(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        try
        {
            await _rules.ReturnReserveLoanAsync(Uid, id);
            return Ok(new { returned = true });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }
}
