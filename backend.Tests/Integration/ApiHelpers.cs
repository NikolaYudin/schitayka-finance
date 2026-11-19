using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SchitayKa.Tests.Integration;

/// <summary>Небольшие помощники для HTTP-вызовов и разбора JSON в интеграционных тестах.</summary>
internal static class ApiHelpers
{
    public static async Task<HttpResponseMessage> GetWithTokenAsync(
        this HttpClient client, string url, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(
        this HttpClient client, string url, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PutJsonAsync(
        this HttpClient client, string url, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>Проверить код ответа и разобрать тело как JSON.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(
        this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(body);
    }

    public static async Task<string> ReadTokenAsync(this HttpResponseMessage response)
    {
        using var json = await response.ReadJsonAsync();
        return json.RootElement.GetProperty("token").GetString()
               ?? throw new InvalidOperationException("В ответе отсутствует токен");
    }

    /// <summary>Выполнить первичную настройку и вернуть токен первого администратора.</summary>
    public static async Task<string> CreateAdminAsync(this HttpClient client)
    {
        using var response = await client.PostJsonAsync("/api/setup/admin",
            new { email = "admin@family.ru", name = "Администратор", password = "StrongPass123!" });
        return await response.ReadTokenAsync();
    }
}
