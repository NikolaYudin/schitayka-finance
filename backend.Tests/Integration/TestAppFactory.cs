using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace SchitayKa.Tests.Integration;

/// <summary>
/// Общая коллекция интеграционных тестов. Запрещаем параллелизм: фабрика настраивает
/// приложение через переменные окружения процесса, поэтому запускать её нужно последовательно.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationCollection
{
    public const string Name = "Integration HTTP tests";
}

/// <summary>
/// Фабрика ASP.NET Core приложения. Точка входа <c>Program</c> — top-level statements,
/// поэтому переменные окружения задаются до создания клиента (до запуска хоста).
/// </summary>
internal sealed class TestAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Явно задаём content root: иначе WebApplicationFactory ищет .deps.json
        // в выходной папке тестов и падает при полной пересборке решения.
        var backendDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "backend"));
        builder.UseContentRoot(backendDir);

        // Тестовое окружение: без Vite-прокси и без демо-сида (последний ещё и флагом выключен).
        builder.UseEnvironment("Testing");
    }
}

/// <summary>
/// Изолированный экземпляр приложения: уникальный файл SQLite во временной папке,
/// свой хост и HTTP-клиент. Удаляет базу и временную папку после завершения теста.
/// </summary>
public sealed class IntegrationApp : IAsyncDisposable
{
    // Дополнительная страховка от гонок с другими тестами, меняющими окружение процесса.
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    private static readonly string[] EnvironmentKeys =
    [
        "ConnectionStrings__Default",
        "Jwt__Key",
        "Jwt__Issuer",
        "Jwt__Audience",
        "SCHITAYKA_SEED_DEMO",
        "SCHITAYKA_AUTOBUILD_FRONTEND",
        "ASPNETCORE_ENVIRONMENT",
    ];

    private readonly TestAppFactory _factory;
    private readonly string _directory;

    private IntegrationApp(TestAppFactory factory, HttpClient client, string directory)
    {
        _factory = factory;
        Client = client;
        _directory = directory;
    }

    public HttpClient Client { get; }

    /// <summary>Создать приложение с чистой базой и уже запущенным тестовым сервером.</summary>
    public static async Task<IntegrationApp> StartAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "schitayka-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "app.db");

        var previous = EnvironmentKeys.ToDictionary(
            key => key, key => Environment.GetEnvironmentVariable(key), StringComparer.Ordinal);

        await EnvironmentLock.WaitAsync();
        TestAppFactory? factory = null;
        HttpClient? client = null;
        try
        {
            // Ключ должен быть достаточно длинным для HS256 (не менее 32 байт).
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={databasePath}");
            Environment.SetEnvironmentVariable("Jwt__Key", "integration-tests-jwt-key-0123456789-abcdefghijklmnop");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "SchitayKa");
            Environment.SetEnvironmentVariable("Jwt__Audience", "SchitayKaClients");
            Environment.SetEnvironmentVariable("SCHITAYKA_SEED_DEMO", "false");
            Environment.SetEnvironmentVariable("SCHITAYKA_AUTOBUILD_FRONTEND", "false");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

            factory = new TestAppFactory();
            // CreateClient() запускает хост: настройки приложения уже прочитаны к этому моменту.
            client = factory.CreateClient();
            return new IntegrationApp(factory, client, directory);
        }
        catch
        {
            client?.Dispose();
            factory?.Dispose();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* временную папку удалим при следующей уборке */ }
            throw;
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            EnvironmentLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _factory.Dispose();

        // SQLite держит файлы открытыми через пул соединений — его нужно сбросить перед удалением.
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 9)
            {
                // Антивирус или пул соединений может временно удерживать файл.
                await Task.Delay(50);
            }
        }
    }
}
