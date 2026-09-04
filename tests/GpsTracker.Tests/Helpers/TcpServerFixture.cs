using System.Net;
using GpsTracker.Configuration;
using GpsTracker.Database;
using GpsTracker.Models;
using GpsTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GpsTracker.Tests.Helpers;

/// <summary>
/// Поднимает настоящий сервер (TcpListenerService) на свободном порту с временной SQLite-базой —
/// заменяет внешний python-симулятор + ручной запуск приложения. Тест подключается TcpClient-ом,
/// шлёт кадры трекера и проверяет записи в БД через этот же хост.
/// </summary>
internal sealed class TcpServerFixture : IAsyncDisposable
{
    private readonly IHost _host;

    private TcpServerFixture(IHost host, int port, string dbPath)
    {
        _host = host;
        Port = port;
        DbPath = dbPath;
    }

    public int Port { get; }

    public string DbPath { get; }

    /// <summary>Запускает сервер на случайном свободном порту с временной базой.</summary>
    public static async Task<TcpServerFixture> StartAsync(CancellationToken ct = default)
    {
        var port = GetFreePort();
        var dbPath = Path.Combine(Path.GetTempPath(), $"gps-tracker-test-{Guid.NewGuid():N}.db");

        var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
                services.Configure<TcpSettings>(s => s.Port = port);
                services.AddHostedService<TcpListenerService>();
            })
            .Build();

        var dbFactory = host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        await host.StartAsync(ct);
        return new TcpServerFixture(host, port, dbPath);
    }

    public async Task<List<GpsPoint>> GetPointsAsync(string imei, CancellationToken ct = default)
    {
        var dbFactory = _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GpsPoints
            .AsNoTracking()
            .Where(p => p.Imei == imei)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    /// <summary>Ждёт, пока в базе появится как минимум count точек с указанным IMEI.</summary>
    public async Task<List<GpsPoint>> WaitForPointsAsync(
        string imei, int count, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var points = await GetPointsAsync(imei, ct);
            if (points.Count >= count || DateTime.UtcNow > deadline)
            {
                return points;
            }
            await Task.Delay(100, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();

        foreach (var file in new[] { DbPath, DbPath + "-shm", DbPath + "-wal" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
