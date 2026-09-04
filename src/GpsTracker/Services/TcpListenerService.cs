using System.Net;
using System.Net.Sockets;
using System.Text;
using GpsTracker.Configuration;
using GpsTracker.Database;
using GpsTracker.Models;
using GpsTracker.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GpsTracker.Services;

public class TcpListenerService : BackgroundService
{
    private const int MaxBufferSize = 64 * 1024;

    private readonly TcpSettings _tcpSettings;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<TcpListenerService> _logger;

    public TcpListenerService(
        IOptions<TcpSettings> tcpSettings,
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<TcpListenerService> logger)
    {
        _tcpSettings = tcpSettings.Value;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _tcpSettings.Port);
        listener.Start();
        _logger.LogInformation("TCP-сервер запущен на порту {Port}", _tcpSettings.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при приёме TCP-подключения");
                continue;
            }

            _ = HandleClientAsync(client, stoppingToken);
        }

        listener.Stop();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            // Соединение без единого байта — healthcheck-проба: логируем на Debug,
            // чтобы не засорять лог (реальный трекер всегда шлёт логин первым).
            var receivedAny = false;

            var buffer = new byte[MaxBufferSize];
            var session = new TrackerSession();

            try
            {
                await using var stream = client.GetStream();
                while (!stoppingToken.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(session.BufferedLength), stoppingToken);
                    if (bytesRead == 0)
                    {
                        if (receivedAny)
                        {
                            _logger.LogInformation("Трекер отключился: {Remote}", remoteEndPoint);
                        }

                        break;
                    }

                    if (!receivedAny)
                    {
                        receivedAny = true;
                        _logger.LogInformation("Трекер подключился: {Remote}", remoteEndPoint);
                    }

                    session.BufferedLength += bytesRead;

                    if (session.Protocol == TrackerProtocol.Unspecified)
                    {
                        session.Protocol = buffer[0] == (byte)'('
                            ? TrackerProtocol.Gt02Text
                            : TrackerProtocol.Gt06Binary;
                        _logger.LogInformation("Протокол трекера {Remote}: {Protocol}", remoteEndPoint, session.Protocol);
                    }

                    session.BufferedLength = session.Protocol == TrackerProtocol.Gt02Text
                        ? await HandleTextProtocolAsync(buffer, session, stream, remoteEndPoint, stoppingToken)
                        : await HandleBinaryProtocolAsync(buffer, session, stream, remoteEndPoint, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Ошибка соединения с трекером {Remote} ({Imei})", remoteEndPoint, session.Imei);
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Сетевая ошибка с трекером {Remote} ({Imei})", remoteEndPoint, session.Imei);
            }
            finally
            {
                if (receivedAny)
                {
                    _logger.LogInformation("Обработка завершена: {Remote} (IMEI {Imei})", remoteEndPoint, session.Imei);
                }
                else
                {
                    // Healthcheck-проба или сканер портов: одна Debug-строка вместо трёх Information
                    _logger.LogDebug("Пустое соединение закрыто: {Remote}", remoteEndPoint);
                }
            }
        }
    }

    /// <summary>
    /// Текстовый протокол ConCox GT02: кадры "( ... )". Ответ "LOAD" — на логин,
    /// на кадры локации протокол ответов не требует.
    /// Возвращает длину буфера с недокачанным хвостом (после последнего ')').
    /// </summary>
    private async Task<int> HandleTextProtocolAsync(
        byte[] buffer,
        TrackerSession session,
        NetworkStream stream,
        string remoteEndPoint,
        CancellationToken ct)
    {
        var bufferedLength = session.BufferedLength;
        var text = Encoding.ASCII.GetString(buffer, 0, bufferedLength);

        var lastComplete = text.LastIndexOf(')');
        if (lastComplete < 0)
        {
            return bufferedLength; // целых кадров пока нет
        }

        foreach (var packet in Gt02TextProtocolParser.Parse(text[..(lastComplete + 1)]))
        {
            if (packet.Type == Gt06PacketType.Login)
            {
                if (!string.IsNullOrEmpty(packet.Imei))
                {
                    session.Imei = packet.Imei;
                }
                _logger.LogInformation("Login (GT02) от трекера {Remote}: IMEI {Imei}", remoteEndPoint, session.Imei);

                var response = Encoding.ASCII.GetBytes(Gt02TextProtocolParser.LoginResponse);
                await stream.WriteAsync(response, ct);
            }
            else
            {
                packet.Imei = session.Imei;
                await SavePointAsync(packet);

                if (packet.Type is Gt06PacketType.Location or Gt06PacketType.Alarm)
                {
                    _logger.LogDebug(
                        "Location (GT02) {Imei}: {Lat:F6}, {Lon:F6}, {Speed} км/ч",
                        packet.Imei, packet.Latitude, packet.Longitude, packet.Speed);
                }
            }
        }

        // Хвост после последнего полного кадра переносим в начало буфера
        var keepLength = bufferedLength - (lastComplete + 1);
        if (keepLength > 0)
        {
            Array.Copy(buffer, lastComplete + 1, buffer, 0, keepLength);
        }
        return keepLength;
    }

    /// <summary>
    /// Бинарный протокол GT06. Возвращает длину буфера с недокачанным хвостом.
    /// </summary>
    private async Task<int> HandleBinaryProtocolAsync(
        byte[] buffer,
        TrackerSession session,
        NetworkStream stream,
        string remoteEndPoint,
        CancellationToken ct)
    {
        var bufferedLength = session.BufferedLength;
        var packets = Gt06PacketParser.Parse(buffer, 0, bufferedLength);

        // Оставляем в буфере только необработанный "хвост" (незавершённый пакет)
        var keepLength = CompactBuffer(buffer, bufferedLength);

        foreach (var packet in packets)
        {
            // В GT06 только login содержит IMEI — запоминаем его на всю сессию
            if (packet.Type == Gt06PacketType.Login && !string.IsNullOrEmpty(packet.Imei))
            {
                session.Imei = packet.Imei;
            }
            else
            {
                packet.Imei = session.Imei;
            }

            var response = await ProcessPacketAsync(packet, remoteEndPoint);
            if (response is not null)
            {
                await stream.WriteAsync(response, ct);
            }
        }

        return keepLength;
    }

    /// <summary>
    /// Переносит необработанный хвост буфера в начало и возвращает его длину.
    /// </summary>
    private static int CompactBuffer(byte[] buffer, int bufferedLength)
    {
        // Незавершённый пакет в GT06 начинается с 0x78 0x78 и заканчивается 0x0D 0x0A.
        // Ищем последний маркер конца пакета: всё после него — недокачанные данные.
        var lastEnd = -1;
        for (var i = 0; i < bufferedLength - 1; i++)
        {
            if (buffer[i] == 0x0D && buffer[i + 1] == 0x0A)
            {
                lastEnd = i + 2;
            }
        }

        if (lastEnd < 0)
        {
            // Полных пакетов нет — оставляем всё как есть (недокачанный пакет)
            return bufferedLength;
        }

        if (lastEnd == bufferedLength)
        {
            return 0; // Буфер содержит только целые пакеты
        }

        Array.Copy(buffer, lastEnd, buffer, 0, bufferedLength - lastEnd);
        return bufferedLength - lastEnd;
    }

    /// <summary>
    /// Обрабатывает пакет бинарного протокола и возвращает байты ответа (ACK), если он требуется.
    /// </summary>
    private async Task<byte[]?> ProcessPacketAsync(Gt06Packet packet, string remoteEndPoint)
    {
        switch (packet.Type)
        {
            case Gt06PacketType.Login:
                _logger.LogInformation("Login от трекера {Remote}: IMEI {Imei}", remoteEndPoint, packet.Imei);
                await SavePointAsync(packet);
                return Gt06PacketParser.CreateLoginResponse(packet.SerialNumber);

            case Gt06PacketType.Location:
            case Gt06PacketType.Alarm:
                _logger.LogDebug(
                    "Location {Imei}: {Lat:F6}, {Lon:F6}, {Speed} км/ч",
                    packet.Imei, packet.Latitude, packet.Longitude, packet.Speed);
                await SavePointAsync(packet);
                return Gt06PacketParser.CreateLocationResponse(packet.SerialNumber);

            case Gt06PacketType.Heartbeat:
                _logger.LogDebug("Heartbeat от трекера {Remote}", remoteEndPoint);
                return Gt06PacketParser.CreateHeartbeatResponse(packet.SerialNumber);

            default:
                _logger.LogDebug(
                    "Пакет 0x{Protocol:X2} от {Remote} — без обработки",
                    packet.ProtocolNumber, remoteEndPoint);
                return null;
        }
    }

    private async Task SavePointAsync(Gt06Packet packet)
    {
        // Пропускаем точки без GPS-фикса: нулевые координаты искажают карту и историю.
        if (packet.Type is not (Gt06PacketType.Location or Gt06PacketType.Alarm))
        {
            return;
        }

        if (Math.Abs(packet.Latitude) < 0.0001 && Math.Abs(packet.Longitude) < 0.0001)
        {
            _logger.LogDebug("Точка без GPS-фикса пропущена (IMEI {Imei})", packet.Imei);
            return;
        }

        var point = new GpsPoint
        {
            Imei = packet.Imei,
            Latitude = packet.Latitude,
            Longitude = packet.Longitude,
            Speed = packet.Speed,
            Course = packet.Course,
            Satellites = packet.Satellites,
            Timestamp = packet.Timestamp,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            db.GpsPoints.Add(point);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить точку (IMEI {Imei})", packet.Imei);
        }
    }
}

/// <summary>Поддерживаемые протоколы трекеров.</summary>
public enum TrackerProtocol
{
    Unspecified,
    Gt06Binary,
    Gt02Text
}

/// <summary>
/// Состояние одного TCP-подключения трекера: выбранный протокол,
/// IMEI с последнего логина и размер недокачанного буфера.
/// </summary>
public class TrackerSession
{
    public TrackerProtocol Protocol { get; set; } = TrackerProtocol.Unspecified;
    public string Imei { get; set; } = "unknown";
    public int BufferedLength { get; set; }
}
