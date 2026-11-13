using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Api.Controllers;

/// <summary>
/// Мониторинг инвестиций и Инвесткопилки (Задача 6):
/// портфели по банкам, активы и сравнение с накопительным счётом.
/// </summary>
[ApiController]
[Route("api/investments")]
[Authorize]
public class InvestmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    public InvestmentsController(AppDbContext db) => _db = db;
    private int Uid => UserAccess.UidOf(User);

    public record PortfolioDto(
        string Name, int BankId, int Product, int Strategy, string Currency,
        decimal? AvgPrice, decimal? CurrentPrice, string? Ticker, int? AssetType);

    public record HoldingDto(
        string Name, string? Ticker, int AssetType, decimal Quantity,
        decimal AvgPrice, decimal CurrentPrice, string Currency);

    public record PayoutNoteDto(string? Note = null);

    private static string ProductName(BankProduct p) => p switch
    {
        BankProduct.Savings => "Накопительный счёт",
        BankProduct.Deposit => "Вклад",
        BankProduct.Broker => "Брокерский счёт",
        BankProduct.IIS => "ИИС",
        BankProduct.MoneyBox => "Инвесткопилка",
        _ => "Прочее",
    };

    private static string StrategyName(MoneyBoxStrategy s) => s switch
    {
        MoneyBoxStrategy.AlwaysPlus => "Всегда в плюсе",
        MoneyBoxStrategy.Balanced => "Сбалансированная",
        MoneyBoxStrategy.StableCompanies => "Стабильные компании",
        _ => "Не выбрана",
    };

    private static object Project(InvestmentPortfolio p, InvestmentCalculator.PortfolioSummary s) => new
    {
        p.Id,
        p.Name,
        bankId = p.BankId,
        bankName = p.Bank.Name,
        product = (int)p.Product,
        productName = ProductName(p.Product),
        strategy = (int)p.Strategy,
        strategyName = StrategyName(p.Strategy),
        strategyHint = InvestmentCalculator.StrategyHint(p.Strategy),
        riskNote = InvestmentCalculator.RiskNote(p.Strategy),
        p.Currency,
        p.CreatedAt,
        s.Invested,
        s.Value,
        s.Pnl,
        s.PnlPct,
        s.CashValue,
        s.InvestedValue,
        s.Days,
        s.SavingsAt6,
        s.SavingsAt8,
        s.RiskPremium,
        s.TaxEstimate,
        holdings = s.Holdings,
    };

    private async Task<InvestmentCalculator.PortfolioSummary> SummaryOf(InvestmentPortfolio p)
    {
        var holdings = await _db.InvestmentHoldings
            .Where(h => h.PortfolioId == p.Id)
            .Select(h => new InvestmentCalculator.HoldingInput(
                h.Name, h.Ticker, h.AssetType, h.Quantity, h.AvgPrice, h.CurrentPrice, h.Currency, h.Id))
            .ToListAsync();

        var days = Math.Max((DateTime.Today - p.CreatedAt.Date).Days, 1);
        return InvestmentCalculator.SummarizePortfolio(holdings, days, p.CreatedAt);
    }

    /// <summary>Все портфели пользователя со сводкой.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (Uid < 0) return Unauthorized(new { error = "Тредуется вход" });
        var portfolios = await _db.InvestmentPortfolios
            .Include(p => p.Bank)
            .Where(p => p.UserId == Uid)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        var result = new List<object>();
        foreach (var p in portfolios)
            result.Add(Project(p, await SummaryOf(p)));
        return Ok(result);
    }

    /// <summary>Сводка по всем портфелям сразу.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var portfolios = await _db.InvestmentPortfolios
            .Include(p => p.Bank)
            .Where(p => p.UserId == Uid)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        var all = new List<InvestmentCalculator.HoldingInput>();
        foreach (var p in portfolios)
        {
            all.AddRange(await _db.InvestmentHoldings
                .Where(h => h.PortfolioId == p.Id)
                .Select(h => new InvestmentCalculator.HoldingInput(
                    h.Name, h.Ticker, h.AssetType, h.Quantity, h.AvgPrice, h.CurrentPrice, h.Currency, h.Id))
                .ToListAsync());
        }

        var days = portfolios.Count == 0 ? 1
            : Math.Max((DateTime.Today - portfolios.Min(p => p.CreatedAt).Date).Days, 1);
        var s = InvestmentCalculator.SummarizePortfolio(all, days);

        return Ok(new
        {
            s.Invested,
            s.Value,
            s.Pnl,
            s.PnlPct,
            s.CashValue,
            s.InvestedValue,
            s.Days,
            s.SavingsAt6,
            s.SavingsAt8,
            s.RiskPremium,
            s.TaxEstimate,
            portfoliosCount = portfolios.Count,
            savingsRate = InvestmentCalculator.SavingsRate,
            savingsRatePro = InvestmentCalculator.SavingsRatePro,
            taxRate = InvestmentCalculator.TaxRate,
        });
    }

    /// <summary>Создать портфель (выбор банка и продукта).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PortfolioDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название портфеля обязательно" });

        var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == dto.BankId && (b.UserId == null || b.UserId == Uid));
        if (bank is null) return BadRequest(new { error = "Банк не найден" });

        var name = dto.Name.Trim();
        if (await _db.InvestmentPortfolios.AnyAsync(p => p.UserId == Uid && p.Name == name))
            return BadRequest(new { error = "Портфель с таким названием уже есть" });

        var portfolio = new InvestmentPortfolio
        {
            UserId = Uid,
            Name = name,
            BankId = bank.Id,
            Product = Enum.IsDefined((BankProduct)dto.Product) ? (BankProduct)dto.Product : BankProduct.Broker,
            Strategy = Enum.IsDefined((MoneyBoxStrategy)dto.Strategy) ? (MoneyBoxStrategy)dto.Strategy : MoneyBoxStrategy.AlwaysPlus,
            Currency = string.IsNullOrWhiteSpace(dto.Currency) ? "RUB" : dto.Currency.Trim().ToUpperInvariant(),
        };
        _db.InvestmentPortfolios.Add(portfolio);
        await _db.SaveChangesAsync();

        return Ok(Project(portfolio, await SummaryOf(portfolio)));
    }

    /// <summary>Изменить портфель.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] PortfolioDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var p = await _db.InvestmentPortfolios.Include(x => x.Bank)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == Uid);
        if (p is null) return NotFound(new { error = "Портфель не найден" });

        var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == dto.BankId && (b.UserId == null || b.UserId == Uid));
        if (bank is null) return BadRequest(new { error = "Банк не найден" });

        p.Name = dto.Name.Trim();
        p.BankId = bank.Id;
        p.Product = Enum.IsDefined((BankProduct)dto.Product) ? (BankProduct)dto.Product : p.Product;
        p.Strategy = Enum.IsDefined((MoneyBoxStrategy)dto.Strategy) ? (MoneyBoxStrategy)dto.Strategy : p.Strategy;
        if (!string.IsNullOrWhiteSpace(dto.Currency)) p.Currency = dto.Currency.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync();

        return Ok(Project(p, await SummaryOf(p)));
    }

    /// <summary>Удалить портфель вместе с позициями.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var p = await _db.InvestmentPortfolios.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Uid);
        if (p is null) return NotFound(new { error = "Портфель не найден" });
        _db.InvestmentPortfolios.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = id });
    }

    /// <summary>Позиции портфеля.</summary>
    [HttpGet("{id:int}/holdings")]
    public async Task<IActionResult> Holdings(int id)
    {
        if (Uid < 0) return Unauthorized(new { error = "Тредуется вход" });
        if (!await _db.InvestmentPortfolios.AnyAsync(p => p.Id == id && p.UserId == Uid))
            return NotFound(new { error = "Портфель не найден" });

        var list = await _db.InvestmentHoldings
            .Where(h => h.PortfolioId == id)
            .OrderBy(h => h.AssetType).ThenBy(h => h.Name)
            .Select(h => new
            {
                h.Id,
                h.Name,
                h.Ticker,
                assetType = (int)h.AssetType,
                h.Quantity,
                h.AvgPrice,
                h.CurrentPrice,
                h.Currency,
                h.UpdatedAt,
            })
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Добавить позицию (бумагу, фонд или деньги копилки).</summary>
    [HttpPost("{id:int}/holdings")]
    public async Task<IActionResult> AddHolding(int id, [FromBody] HoldingDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Тредуется вход" });
        var portfolio = await _db.InvestmentPortfolios.FirstOrDefaultAsync(p => p.Id == id && p.UserId == Uid);
        if (portfolio is null) return NotFound(new { error = "Портфель не найден" });
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { error = "Название позиции обязательно" });
        if (dto.Quantity <= 0) return BadRequest(new { error = "Количество должно быть больше нуля" });

        var assetType = Enum.IsDefined((AssetType)dto.AssetType) ? (AssetType)dto.AssetType : AssetType.Other;
        var holding = new InvestmentHolding
        {
            UserId = Uid,
            PortfolioId = portfolio.Id,
            Name = dto.Name.Trim(),
            Ticker = dto.Ticker?.Trim() ?? "",
            AssetType = assetType,
            Quantity = dto.Quantity,
            AvgPrice = dto.AvgPrice,
            CurrentPrice = dto.CurrentPrice,
            Currency = string.IsNullOrWhiteSpace(dto.Currency) ? portfolio.Currency : dto.Currency.Trim().ToUpperInvariant(),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.InvestmentHoldings.Add(holding);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            holding.Id,
            holding.Name,
            holding.Ticker,
            assetType = (int)holding.AssetType,
            holding.Quantity,
            holding.AvgPrice,
            holding.CurrentPrice,
            holding.Currency,
            holding.UpdatedAt,
        });
    }

    /// <summary>Изменить позицию (в том числе обновить текущую цену).</summary>
    [HttpPut("{id:int}/holdings/{hid:int}")]
    public async Task<IActionResult> UpdateHolding(int id, int hid, [FromBody] HoldingDto dto)
    {
        if (Uid < 0) return Unauthorized(new { error = "Тредуется вход" });
        var h = await _db.InvestmentHoldings.FirstOrDefaultAsync(x => x.Id == hid && x.PortfolioId == id && x.UserId == Uid);
        if (h is null) return NotFound(new { error = "Позиция не найдена" });

        h.Name = dto.Name.Trim();
        h.Ticker = dto.Ticker?.Trim() ?? "";
        if (Enum.IsDefined((AssetType)dto.AssetType)) h.AssetType = (AssetType)dto.AssetType;
        h.Quantity = dto.Quantity;
        h.AvgPrice = dto.AvgPrice;
        h.CurrentPrice = dto.CurrentPrice;
        if (!string.IsNullOrWhiteSpace(dto.Currency)) h.Currency = dto.Currency.Trim().ToUpperInvariant();
        h.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new
        {
            h.Id,
            h.Name,
            h.Ticker,
            assetType = (int)h.AssetType,
            h.Quantity,
            h.AvgPrice,
            h.CurrentPrice,
            h.Currency,
            h.UpdatedAt,
        });
    }

    /// <summary>Удалить позицию.</summary>
    [HttpDelete("{id:int}/holdings/{hid:int}")]
    public async Task<IActionResult> DeleteHolding(int id, int hid)
    {
        if (Uid < 0) return Unauthorized(new { error = "Требуется вход" });
        var h = await _db.InvestmentHoldings.FirstOrDefaultAsync(x => x.Id == hid && x.PortfolioId == id && x.UserId == Uid);
        if (h is null) return NotFound(new { error = "Позиция не найдена" });
        _db.InvestmentHoldings.Remove(h);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = hid });
    }
}
