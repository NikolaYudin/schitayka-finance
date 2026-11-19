using System.Net;

namespace SchitayKa.Tests.Integration;

/// <summary>Настройки интерфейса и экспорт профиля через полный HTTP-конвейер.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class SettingsAndExportTests
{
    [Fact]
    public async Task Appearance_GetPutGet_RoundTripsTheme()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;
        var token = await client.CreateAdminAsync();

        using var initialResponse = await client.GetWithTokenAsync("/api/settings/appearance", token);
        using var initial = await initialResponse.ReadJsonAsync();
        Assert.Equal("system", initial.RootElement.GetProperty("theme").GetString());

        using var putResponse = await client.PutJsonAsync(
            "/api/settings/appearance", new { theme = "dark" }, token);
        using var put = await putResponse.ReadJsonAsync();
        Assert.Equal("dark", put.RootElement.GetProperty("theme").GetString());

        using var afterResponse = await client.GetWithTokenAsync("/api/settings/appearance", token);
        using var after = await afterResponse.ReadJsonAsync();
        Assert.Equal("dark", after.RootElement.GetProperty("theme").GetString());

        // Недопустимое значение нормализуется сервером в "system".
        using var invalidResponse = await client.PutJsonAsync(
            "/api/settings/appearance", new { theme = "neon" }, token);
        using var invalid = await invalidResponse.ReadJsonAsync();
        Assert.Equal("system", invalid.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task ExportProfile_AnonymizeAndTake_ReturnsSchemaVersion2()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;
        var token = await client.CreateAdminAsync();

        using var response = await client.GetWithTokenAsync(
            "/api/export/profile?anonymize=true&take=5", token);
        using var json = await response.ReadJsonAsync();
        var root = json.RootElement;

        var meta = root.GetProperty("meta");
        Assert.Equal(2, meta.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("СчитайКа", meta.GetProperty("application").GetString());

        var parameters = meta.GetProperty("exportParams");
        Assert.True(parameters.GetProperty("anonymize").GetBoolean());
        Assert.Equal(5, parameters.GetProperty("take").GetInt32());

        // При обезличивании email заменяется, а операций не больше запрошенного лимита.
        Assert.Equal("anonymous@example.invalid", root.GetProperty("user").GetProperty("email").GetString());
        Assert.True(root.GetProperty("transactions").GetArrayLength() <= 5);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("Cache-Control", out var cache)
                    && cache.Any(v => v.Contains("no-store", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ExportProfile_RequiresAuth_AndValidatesDateParameters()
    {
        await using var app = await IntegrationApp.StartAsync();
        var client = app.Client;

        using var anonymousResponse = await client.GetWithTokenAsync("/api/export/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var token = await client.CreateAdminAsync();
        using var invalidDateResponse = await client.GetWithTokenAsync(
            "/api/export/profile?from=not-a-date", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDateResponse.StatusCode);
    }
}
