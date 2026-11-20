using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SchitayKa.Mobile.Models;

namespace SchitayKa.Mobile.Services;

/// <summary>
/// Клиент REST API СчитайКа. Адрес сервера берётся из настроек на каждый запрос,
/// поэтому его можно менять без пересборки приложения.
/// </summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly AuthState _auth;
    private readonly AppSettings _settings;

    public ApiClient(HttpClient http, AuthState auth, AppSettings settings)
    {
        _http = http;
        _auth = auth;
        _settings = settings;
    }

    private Uri Url(string path) =>
        new(new Uri(_settings.ServerUrl + "/"), path.TrimStart('/'));

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (!string.IsNullOrEmpty(_auth.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = await RequestAsync(method, path, body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                await _auth.LogoutAsync();

            string? message = null;
            try
            {
                var error = JsonSerializer.Deserialize<ApiError>(text, Json);
                message = error?.Error ?? error?.Title;
            }
            catch { /* сервер вернул не JSON */ }

            throw new ApiException((int)response.StatusCode,
                message ?? $"Ошибка сервера ({response.StatusCode})");
        }

        if (string.IsNullOrWhiteSpace(text))
            return default!;

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new ApiException(500, "Пустой ответ сервера");
    }

    public Task<SetupStatus> GetSetupStatusAsync() =>
        SendAsync<SetupStatus>(HttpMethod.Get, "api/setup/status");

    public Task<LoginResponse> LoginAsync(string email, string password) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "api/auth/login",
            new { email, password });

    public Task<LoginResponse> CreateAdminAsync(string email, string name, string password) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "api/setup/admin",
            new { email, name, password });

    public Task<UserInfo> GetMeAsync() =>
        SendAsync<UserInfo>(HttpMethod.Get, "api/auth/me");

    public Task<Summary> GetSummaryAsync(int year, int month, string scope = "me") =>
        SendAsync<Summary>(HttpMethod.Get,
            $"api/budget/summary?year={year}&month={month}&scope={scope}");

    public Task<List<TransactionRow>> GetTransactionsAsync(int month, int year, int take = 20) =>
        SendAsync<List<TransactionRow>>(HttpMethod.Get,
            $"api/transactions?month={month}&year={year}&take={take}");

    public Task<List<AccountRow>> GetAccountsAsync() =>
        SendAsync<List<AccountRow>>(HttpMethod.Get, "api/accounts");

    public Task<List<CategoryRow>> GetCategoriesAsync() =>
        SendAsync<List<CategoryRow>>(HttpMethod.Get, "api/categories");

    public Task<TransactionCreated> CreateTransactionAsync(TransactionRequest request) =>
        SendAsync<TransactionCreated>(HttpMethod.Post, "api/transactions", request);

    public Task<TransactionCreated> UpdateTransactionAsync(int id, TransactionRequest request) =>
        SendAsync<TransactionCreated>(HttpMethod.Put, $"api/transactions/{id}", request);

    public Task DeleteTransactionAsync(int id) =>
        SendAsync<object>(HttpMethod.Delete, $"api/transactions/{id}");

    public Task ChangePasswordAsync(string current, string next) =>
        SendAsync<object>(HttpMethod.Post, "api/auth/change-password",
            new { currentPassword = current, newPassword = next });
}

/// <summary>Ошибка запроса к API с кодом HTTP.</summary>
public sealed class ApiException : Exception
{
    public int StatusCode { get; }

    public ApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
