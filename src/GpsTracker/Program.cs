using GpsTracker.Configuration;
using GpsTracker.Database;
using GpsTracker.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// Настройки
builder.Services.Configure<TcpSettings>(builder.Configuration.GetSection(TcpSettings.SectionName));
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection(TelegramSettings.SectionName));
builder.Services.Configure<MapSettings>(builder.Configuration.GetSection(MapSettings.SectionName));

// База данных (SQLite). Фабрика нужна фоновым службам для параллельного доступа
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// HTTP-клиент для загрузки тайлов карты
builder.Services.AddHttpClient("tiles", client =>
{
    var userAgent = builder.Configuration["Map:UserAgent"] ?? "GpsTracker/1.0";
    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Сервисы
builder.Services.AddSingleton<IMapRendererService, MapRendererService>();
builder.Services.AddHostedService<TcpListenerService>();
builder.Services.AddHostedService<TelegramBotService>();

// Каталог для БД: SQLite не создаёт его сам, при отсутствии пути упадёт EnsureCreated
var dbPath = builder.Configuration.GetConnectionString("Default") ?? string.Empty;
if (dbPath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    var dbFile = dbPath["Data Source=".Length..];
    var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbFile));
    if (!string.IsNullOrEmpty(dbDir))
    {
        Directory.CreateDirectory(dbDir);
    }
}

var host = builder.Build();

// Автомиграция БД при старте
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();
