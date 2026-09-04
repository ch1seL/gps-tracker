using GpsTracker.Models;
using GpsTracker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

// Тестовая консоль для проверки рендеринга карт без запуска TCP/Telegram.
using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GpsTracker-MapTest/1.0");

IMapRendererService renderer = new MapRendererService(
    Options.Create(new GpsTracker.Configuration.MapSettings
    {
        CachePath = "./tile-cache-test",
        DefaultZoom = 15
    }),
    new FixedHttpClientFactory(httpClient),
    NullLogger<MapRendererService>.Instance);

// Москва, круговой маршрут (как в simulator.py)
var center = (lat: 55.751244, lon: 37.618400);
var points = new List<GpsPoint>();
for (int i = 0; i < 60; i++)
{
    var angle = i * Math.PI / 30;
    points.Add(new GpsPoint
    {
        Imei = "8681201234567890",
        Latitude = center.lat + 0.01 * Math.Sin(angle),
        Longitude = center.lon + 0.01 * Math.Cos(angle),
        Speed = 10 + 80 * Math.Abs(Math.Sin(angle * 2)),
        Course = (angle * 180 / Math.PI + 90) % 360,
        Satellites = 10,
        Timestamp = DateTime.UtcNow.AddMinutes(-60 + i)
    });
}

// 1. Позиция
var positionPng = await renderer.RenderPositionAsync(points[^1]);
await File.WriteAllBytesAsync("map-position.png", positionPng);
Console.WriteLine($"position.png: {positionPng.Length} bytes");

// 1a. Позиция с уменьшенным масштабом (/pos 2.5)
var positionZoomedPng = await renderer.RenderPositionAsync(points[^1], zoomOutFactor: 2.5);
await File.WriteAllBytesAsync("map-position-2.5x.png", positionZoomedPng);
Console.WriteLine($"position-2.5x.png: {positionZoomedPng.Length} bytes");

// 2. История за весь круг
var trackPng = await renderer.RenderTrackAsync(points);
await File.WriteAllBytesAsync("map-history.png", trackPng);
Console.WriteLine($"history.png: {trackPng.Length} bytes");

Console.WriteLine("OK");

/// <summary>Заглушка IHttpClientFactory для теста.</summary>
sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
