using Microsoft.Maui.Storage;

namespace SchitayKa.Mobile.Services;

/// <summary>
/// Настройки подключения к серверу СчитайКа.
/// Эмулятор Android обращается к хосту через 10.0.2.2; на реальном телефоне
/// пользователь указывает IP NAS в локальной сети.
/// </summary>
public sealed class AppSettings
{
    private const string ServerKey = "server_url";
    private const string DefaultServer = "http://10.0.2.2:5023";

    public string ServerUrl
    {
        get => Preferences.Get(ServerKey, DefaultServer);
        set
        {
            var url = (value ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) url = DefaultServer;
            Preferences.Set(ServerKey, url);
        }
    }

    public static string DefaultServerUrl => DefaultServer;
}
