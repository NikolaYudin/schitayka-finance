using System.Text.Json;
using Microsoft.Maui.Storage;
using SchitayKa.Mobile.Models;

namespace SchitayKa.Mobile.Services;

/// <summary>
/// Сессия пользователя. JWT хранится в защищённом хранилище устройства
/// (Android Keystore), а не в sessionStorage — токен переживает перезапуск приложения.
/// </summary>
public sealed class AuthState
{
    private const string TokenKey = "sk_token";
    private const string UserKey = "sk_user";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Token { get; private set; }
    public UserInfo? User { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

    public event Action? Changed;

    /// <summary>Восстановить сессию при запуске приложения.</summary>
    public async Task InitializeAsync()
    {
        if (Token is not null) return;
        Token = await SecureStorage.Default.GetAsync(TokenKey);
        var json = Preferences.Get(UserKey, "");
        if (!string.IsNullOrEmpty(json))
        {
            try { User = JsonSerializer.Deserialize<UserInfo>(json, Json); }
            catch { /* повреждённый кэш — просто запросим профиль заново */ }
        }
        Changed?.Invoke();
    }

    public async Task SetSessionAsync(string token, UserInfo user)
    {
        Token = token;
        User = user;
        await SecureStorage.Default.SetAsync(TokenKey, token);
        Preferences.Set(UserKey, JsonSerializer.Serialize(user, Json));
        Changed?.Invoke();
    }

    public void UpdateUser(UserInfo user)
    {
        User = user;
        Preferences.Set(UserKey, JsonSerializer.Serialize(user, Json));
        Changed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        Token = null;
        User = null;
        SecureStorage.Default.Remove(TokenKey);
        Preferences.Remove(UserKey);
        await Task.CompletedTask;
        Changed?.Invoke();
    }
}
