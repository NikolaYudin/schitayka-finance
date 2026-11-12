using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SchitayKa.Api.Data;

// Нужен только инструментам EF Core (dotnet ef migrations add/update):
// позволяет создавать контекст без запуска Program.cs и без подключения к живой БД.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Рабочая папка design-time может быть и корнем репозитория, и backend/,
        // поэтому путь к БД строим от корня решения, а не от текущего каталога.
        var root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "SchitayKa.sln")))
            root = Directory.GetParent(root)?.FullName;
        root ??= Directory.GetCurrentDirectory();

        var dbPath = Path.Combine(root, "backend", "data", "app.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var connectionString = Environment.GetEnvironmentVariable("SCHITAYKA_DB")
                               ?? $"Data Source={dbPath}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new AppDbContext(options);
    }
}
