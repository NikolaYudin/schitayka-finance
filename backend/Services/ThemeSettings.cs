namespace SchitayKa.Api.Services;

/// <summary>Допустимые значения темы и её нормализация (B6).</summary>
public static class ThemeSettings
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? value) => value switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };
}
