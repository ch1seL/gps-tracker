using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GpsTracker.Configuration;
using GpsTracker.Database;
using GpsTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GpsTracker.Services;

public class TelegramBotService : BackgroundService
{
    private readonly TelegramSettings _settings;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IMapRendererService _mapRenderer;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        IOptions<TelegramSettings> settings,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IMapRendererService mapRenderer,
        ILogger<TelegramBotService> logger)
    {
        _settings = settings.Value;
        _dbContextFactory = dbContextFactory;
        _mapRenderer = mapRenderer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BotToken) ||
            _settings.BotToken == "YOUR_BOT_TOKEN")
        {
            _logger.LogWarning("Telegram BotToken не задан — бот не запущен. Укажите токен в appsettings.json");
            return;
        }

        var bot = new TelegramBotClient(_settings.BotToken);

        bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
            cancellationToken: stoppingToken);

        _logger.LogInformation("Telegram-бот запущен");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Message is not { } message || message.Text is not { } text)
            {
                return;
            }

            // Проверка доступа: пустой список = разрешены все чаты
            if (_settings.AllowedChatIds.Length > 0 &&
                !_settings.AllowedChatIds.Contains(message.Chat.Id))
            {
                _logger.LogWarning("Доступ запрещён для chat {ChatId}", message.Chat.Id);
                return;
            }

            var chatId = message.Chat.Id;

            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendMessage(chatId,
                    """
                    🛰 GPS-трекер

                    /pos — текущая позиция на карте
                    /history — история движения за сутки
                    /history 2d — история за 2 дня
                    /history 6h — история за 6 часов
                    /history 2024-01-15 2024-01-16 — история за период
                    """,
                    cancellationToken: ct);
            }
            else if (text.StartsWith("/pos", StringComparison.OrdinalIgnoreCase))
            {
                await SendPositionAsync(bot, chatId, ct);
            }
            else if (text.StartsWith("/history", StringComparison.OrdinalIgnoreCase))
            {
                await SendHistoryAsync(bot, chatId, text, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки обновления Telegram");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiEx => $"Telegram API: {apiEx.ErrorCode} {apiEx.Message}",
            _ => exception.Message
        };

        _logger.LogError("Ошибка polling Telegram: {Error}", errorMessage);
        return Task.CompletedTask;
    }

    private async Task SendPositionAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var lastPoint = await db.GpsPoints
            .OrderByDescending(p => p.Timestamp)
            .FirstOrDefaultAsync(ct);

        if (lastPoint is null)
        {
            await bot.SendMessage(chatId, "Пока нет данных о позиции.", cancellationToken: ct);
            return;
        }

        var caption = BuildPositionCaption(lastPoint);

        try
        {
            var png = await _mapRenderer.RenderPositionAsync(lastPoint, ct);
            using var stream = new MemoryStream(png);
            var photo = new InputFileStream(stream, "position.png");
            await bot.SendPhoto(chatId, photo, caption: caption, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отрисовать карту позиции");
            await bot.SendMessage(chatId, caption, cancellationToken: ct);
        }
    }

    private async Task SendHistoryAsync(ITelegramBotClient bot, long chatId, string command, CancellationToken ct)
    {
        var period = ParseHistoryPeriod(command);
        if (period is null)
        {
            await bot.SendMessage(chatId,
                "Не удалось разобрать период. Примеры:\n" +
                "/history — за сутки\n" +
                "/history 2d — за 2 дня\n" +
                "/history 6h — за 6 часов\n" +
                "/history 2024-01-15 2024-01-16 — за период",
                cancellationToken: ct);
            return;
        }

        var (from, to) = period.Value;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var points = await db.GpsPoints
            .Where(p => p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .ToListAsync(ct);

        if (points.Count == 0)
        {
            await bot.SendMessage(chatId, "За этот период нет точек движения.", cancellationToken: ct);
            return;
        }

        var caption = BuildHistoryCaption(points, from, to);

        try
        {
            var png = await _mapRenderer.RenderTrackAsync(points, ct);
            using var stream = new MemoryStream(png);
            var photo = new InputFileStream(stream, "history.png");
            await bot.SendPhoto(chatId, photo, caption: caption, parseMode: ParseMode.Html, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отрисовать карту истории");
            await bot.SendMessage(chatId, caption, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    // ---------- Разбор периода ----------

    /// <summary>
    /// Поддерживаемые форматы: (пусто) = сутки, 2d = 2 дня, 6h = 6 часов,
    /// 30m = 30 минут, "2024-01-15 2024-01-16" = конкретный период.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To)? ParseHistoryPeriod(string command)
    {
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var now = DateTimeOffset.UtcNow;

        switch (args.Length)
        {
            case 0:
                return (now.AddDays(-1), now);

            case 1 when Regex.IsMatch(args[0], @"^\d+[dhm]$", RegexOptions.IgnoreCase):
                var value = int.Parse(args[0][..^1], CultureInfo.InvariantCulture);
                var unit = char.ToLowerInvariant(args[0][^1]);
                var span = unit switch
                {
                    'd' => TimeSpan.FromDays(value),
                    'h' => TimeSpan.FromHours(value),
                    _ => TimeSpan.FromMinutes(value)
                };
                return (now - span, now);

            case 2 when
                DateTimeOffset.TryParse(args[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from) &&
                DateTimeOffset.TryParse(args[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to):
                return (from, to < from ? to.AddDays(1) : to); // дата без времени = конец дня

            default:
                return null;
        }
    }

    // ---------- Подписи к изображениям ----------

    private static string BuildPositionCaption(GpsPoint p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📍 Текущая позиция");
        sb.AppendLine($"Время: {p.Timestamp:dd.MM.yyyy HH:mm:ss} UTC");
        sb.AppendLine($"Координаты: {p.Latitude:F6}, {p.Longitude:F6}");
        sb.AppendLine($"Скорость: {p.Speed:F0} км/ч");
        sb.AppendLine($"Спутники: {p.Satellites}");
        sb.Append($"[Открыть на карте](https://www.openstreetmap.org/?mlat={p.Latitude:F6}&mlon={p.Longitude:F6}#map=17/{p.Latitude:F6}/{p.Longitude:F6})");
        return sb.ToString();
    }

    private static string BuildHistoryCaption(IReadOnlyList<GpsPoint> points, DateTimeOffset from, DateTimeOffset to)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🛣 История движения: {from:dd.MM.yyyy HH:mm} — {to:dd.MM.yyyy HH:mm} UTC");
        sb.AppendLine($"Точек: {points.Count}");

        var distance = TrackStatistics.CalculateDistanceKm(points);
        var maxSpeed = points.Max(p => p.Speed);
        var avgSpeed = points.Average(p => p.Speed);

        sb.AppendLine($"Расстояние: {distance:F1} км");
        sb.AppendLine($"Макс. скорость: {maxSpeed:F0} км/ч");
        sb.AppendLine($"Средняя скорость: {avgSpeed:F0} км/ч");
        sb.AppendLine();
        sb.Append("Цвет линии: <b>зелёный</b> &lt;30, <b>жёлтый</b> 30–60, <b>оранжевый</b> 60–90, <b>красный</b> 90+ км/ч");

        return sb.ToString();
    }
}

/// <summary>Статистика трека: расстояние по формуле гаверсинуса.</summary>
public static class TrackStatistics
{
    public static double CalculateDistanceKm(IReadOnlyList<GpsPoint> points)
    {
        double totalKm = 0;
        for (var i = 1; i < points.Count; i++)
        {
            totalKm += HaversineKm(
                points[i - 1].Latitude, points[i - 1].Longitude,
                points[i].Latitude, points[i].Longitude);
        }
        return totalKm;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
