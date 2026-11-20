using Microsoft.Extensions.Logging;
using SchitayKa.Mobile.Services;

namespace SchitayKa.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Сервисы приложения
        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<AuthState>();
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<OfflineCache>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
