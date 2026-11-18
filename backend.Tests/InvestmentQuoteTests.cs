using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SchitayKa.Api.Data;
using SchitayKa.Api.Services;

namespace SchitayKa.Tests;

/// <summary>
/// Котировки инвестиций (пункты 1.0–1.1 плана): парсинг MOEX ISS и сервис обновления цен.
/// </summary>
public class InvestmentQuoteTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;

    public InvestmentQuoteTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private sealed class FakeQuoteProvider : IQuoteProvider
    {
        public string Name => "Тестовый источник";
        public Dictionary<string, QuoteLookup> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<QuoteLookup> GetAsync(string ticker, bool force, CancellationToken ct = default) =>
            Task.FromResult(Responses.TryGetValue(ticker, out var v)
                ? v
                : QuoteLookup.NotFound("Тикер не найден."));
    }

    private InvestmentQuoteService Service(IQuoteProvider provider) =>
        new(_db, provider, NullLogger<InvestmentQuoteService>.Instance);

    private async Task<(User User, InvestmentPortfolio Portfolio)> SeedAsync()
    {
        var user = new User { Email = "q@schitayka.ru", Name = "Тест", PasswordHash = "x:y" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var bank = new Bank { Name = "Тест банк", Code = "test", IsSystem = true };
        _db.Banks.Add(bank);
        await _db.SaveChangesAsync();

        var portfolio = new InvestmentPortfolio
        {
            UserId = user.Id, Name = "Портфель", BankId = bank.Id, Product = BankProduct.Broker,
        };
        _db.InvestmentPortfolios.Add(portfolio);
        await _db.SaveChangesAsync();
        return (user, portfolio);
    }

    private async Task<InvestmentHolding> AddHoldingAsync(
        int userId, int portfolioId, string ticker, AssetType type, decimal currentPrice)
    {
        var h = new InvestmentHolding
        {
            UserId = userId, PortfolioId = portfolioId, Name = ticker,
            Ticker = ticker, AssetType = type, Quantity = 10, AvgPrice = 100,
            CurrentPrice = currentPrice, Currency = "RUB",
        };
        _db.InvestmentHoldings.Add(h);
        await _db.SaveChangesAsync();
        return h;
    }

    // ---------- Парсинг MOEX ISS ----------

    [Fact]
    public void TryParsePrice_ReadsPrevPriceAndName()
    {
        const string json = """
            {"securities":{"columns":["SECID","SHORTNAME","PREVPRICE"],
            "data":[["SBER","Сбербанк",275.25]]}}
            """;

        Assert.True(MoexIssQuoteProvider.TryParsePrice(json, out var price, out var name));
        Assert.Equal(275.25m, price);
        Assert.Equal("Сбербанк", name);
    }

    [Fact]
    public void TryParsePrice_SkipsRowsWithoutPrice()
    {
        const string json = """
            {"securities":{"columns":["SECID","SHORTNAME","PREVPRICE"],
            "data":[["SBER","Сбербанк",0],["LKOH","Лукойл",7500]]}}
            """;

        Assert.True(MoexIssQuoteProvider.TryParsePrice(json, out var price, out var name));
        Assert.Equal(7500m, price);
        Assert.Equal("Лукойл", name);
    }

    [Fact]
    public void TryParsePrice_InvalidJson_ReturnsFalse()
    {
        Assert.False(MoexIssQuoteProvider.TryParsePrice("не json", out _, out _));
        Assert.False(MoexIssQuoteProvider.TryParsePrice("""{"securities":{"columns":[],"data":[]}}""", out _, out _));
    }

    // ---------- Сервис обновления ----------

    [Fact]
    public async Task Refresh_FoundQuote_UpdatesPriceSourceAndTime()
    {
        var (user, portfolio) = await SeedAsync();
        await AddHoldingAsync(user.Id, portfolio.Id, "SBER", AssetType.Share, currentPrice: 0m);

        var provider = new FakeQuoteProvider
        {
            Responses = { ["SBER"] = QuoteLookup.Found(new QuoteResult(275.25m, "MOEX ISS")) },
        };

        var r = await Service(provider).RefreshAsync(user.Id, force: false);

        Assert.Equal(1, r.Updated);
        Assert.Equal(0, r.Failed);
        var h = await _db.InvestmentHoldings.SingleAsync();
        Assert.Equal(275.25m, h.CurrentPrice);
        Assert.Equal(QuoteSource.MoexIss, h.QuoteSource);
        Assert.NotNull(h.PriceUpdatedAt);
        Assert.Null(h.QuoteError);
        Assert.NotNull(r.LastSuccessAt);
    }

    [Fact]
    public async Task Refresh_NotFound_KeepsManualPriceAndStoresError()
    {
        var (user, portfolio) = await SeedAsync();
        await AddHoldingAsync(user.Id, portfolio.Id, "UNKNOWN", AssetType.Share, currentPrice: 123m);

        var provider = new FakeQuoteProvider(); // все тикеры — NotFound

        var r = await Service(provider).RefreshAsync(user.Id, force: true);

        Assert.Equal(0, r.Updated);
        Assert.Equal(1, r.Failed);
        var h = await _db.InvestmentHoldings.SingleAsync();
        Assert.Equal(123m, h.CurrentPrice);           // ручная цена не затирается
        Assert.Null(h.QuoteSource);
        Assert.NotNull(h.QuoteError);
        Assert.NotNull(r.LastError);
    }

    [Fact]
    public async Task Refresh_Unavailable_StopsAndKeepsPrices()
    {
        var (user, portfolio) = await SeedAsync();
        await AddHoldingAsync(user.Id, portfolio.Id, "SBER", AssetType.Share, currentPrice: 100m);
        await AddHoldingAsync(user.Id, portfolio.Id, "GAZP", AssetType.Share, currentPrice: 100m);

        var provider = new FakeQuoteProvider
        {
            Responses =
            {
                ["SBER"] = QuoteLookup.Unavailable("MOEX ISS временно недоступен."),
                ["GAZP"] = QuoteLookup.Found(new QuoteResult(200m, "MOEX ISS")),
            },
        };

        var r = await Service(provider).RefreshAsync(user.Id, force: true);

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Updated);                   // второй тикер не запрашивался
        var sber = await _db.InvestmentHoldings.SingleAsync(h => h.Ticker == "SBER");
        Assert.Equal(100m, sber.CurrentPrice);
        Assert.NotNull(sber.QuoteError);
    }

    [Fact]
    public async Task Refresh_FreshPrice_Skipped_UnlessForced()
    {
        var (user, portfolio) = await SeedAsync();
        await AddHoldingAsync(user.Id, portfolio.Id, "SBER", AssetType.Share, currentPrice: 100m);

        var provider = new FakeQuoteProvider
        {
            Responses = { ["SBER"] = QuoteLookup.Found(new QuoteResult(275.25m, "MOEX ISS")) },
        };
        var svc = Service(provider);

        var first = await svc.RefreshAsync(user.Id, force: true);
        Assert.Equal(1, first.Updated);

        var second = await svc.RefreshAsync(user.Id, force: false);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Skipped);              // цена свежая — не дёргаем источник

        var forced = await svc.RefreshAsync(user.Id, force: true);
        Assert.Equal(1, forced.Updated);
    }

    [Fact]
    public async Task Refresh_CashAndEmptyTicker_AreSkipped()
    {
        var (user, portfolio) = await SeedAsync();
        // Деньги копилки не участвуют в котировках вовсе (нет тикера, цена = 1).
        await AddHoldingAsync(user.Id, portfolio.Id, "", AssetType.Cash, currentPrice: 0m);
        // Пустая метка у обычной позиции — пропускаем.
        await AddHoldingAsync(user.Id, portfolio.Id, "", AssetType.Other, currentPrice: 0m);
        await AddHoldingAsync(user.Id, portfolio.Id, "SBER", AssetType.Share, currentPrice: 0m);

        var provider = new FakeQuoteProvider
        {
            Responses = { ["SBER"] = QuoteLookup.Found(new QuoteResult(275.25m, "MOEX ISS")) },
        };

        var r = await Service(provider).RefreshAsync(user.Id, force: true);

        Assert.Equal(1, r.Quoted);
        Assert.Equal(1, r.Skipped);                   // позиция с пустым тикером
        Assert.Equal(1, r.Updated);
    }

    [Fact]
    public async Task Status_ReportsLastSuccessAndError()
    {
        var (user, portfolio) = await SeedAsync();
        await AddHoldingAsync(user.Id, portfolio.Id, "SBER", AssetType.Share, currentPrice: 0m);

        var provider = new FakeQuoteProvider
        {
            Responses =
            {
                ["SBER"] = QuoteLookup.Found(new QuoteResult(275.25m, "MOEX ISS")),
                ["BAD"] = QuoteLookup.NotFound("Тикер не найден."),
            },
        };
        var svc = Service(provider);
        await svc.RefreshAsync(user.Id, force: true);
        await AddHoldingAsync(user.Id, portfolio.Id, "BAD", AssetType.Share, currentPrice: 50m);
        await svc.RefreshAsync(user.Id, force: true);

        var (lastSuccess, lastError) = await svc.StatusAsync(user.Id);
        Assert.NotNull(lastSuccess);
        Assert.NotNull(lastError);
    }
}
