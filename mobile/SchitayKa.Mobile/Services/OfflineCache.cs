using System.Text.Json;
using Microsoft.Maui.Storage;

namespace SchitayKa.Mobile.Services;

/// <summary>
/// Простой офлайн-кэш последних удачно загруженных данных (M4).
/// Используется только когда сервер недоступен — пользователь видит
/// сохранённую копию с пояснением, а не пустой экран.
/// </summary>
public sealed class OfflineCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Save<T>(string key, T value)
    {
        try { Preferences.Set(key, JsonSerializer.Serialize(value, Json)); }
        catch { /* кэш — best effort */ }
    }

    public T? Load<T>(string key)
    {
        try
        {
            var text = Preferences.Get(key, "");
            return string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
        }
        catch { return default; }
    }
}
