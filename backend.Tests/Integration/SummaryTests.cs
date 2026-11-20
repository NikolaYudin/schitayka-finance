namespace SchitayKa.Tests.Integration;

/// <summary>
/// Регрессионный тест: контроллер BudgetController должен создаваться
/// DI без ошибки «Multiple constructors» (B5).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SummaryTests
{
    [Fact]
    public async Task BudgetSummary_ReturnsOk_WithRealDiContainer()
    {
        await using var app = await IntegrationApp.StartAsync();
        var token = await app.Client.CreateAdminAsync();

        using var response = await app.Client.GetWithTokenAsync(
            "/api/budget/summary?year=2026&month=11&scope=me", token);

        using var json = await response.ReadJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("monthIncome", out _));
        Assert.True(json.RootElement.TryGetProperty("totalBalance", out _));
        Assert.True(json.RootElement.TryGetProperty("recommendations", out _));
    }
}
