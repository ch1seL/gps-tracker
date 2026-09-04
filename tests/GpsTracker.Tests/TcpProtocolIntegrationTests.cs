using GpsTracker.Protocol;
using GpsTracker.Tests.Helpers;
using Xunit;

namespace GpsTracker.Tests;

/// <summary>
/// Интеграционные тесты TCP-сервера, заменяющие python-симулятор (tools/simulator.py):
/// поднимается настоящий сервер с TcpListenerService на свободном порту, тест подключается
/// как трекер, отправляет кадры обоих протоколов и проверяет записи в SQLite.
/// </summary>
public class TcpProtocolIntegrationTests : IAsyncLifetime
{
    private TcpServerFixture _server = null!;

    public async Task InitializeAsync() => _server = await TcpServerFixture.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    // ---------------------------------------------------------------- GT02 (текстовый)

    [Fact]
    public async Task Gt02_LoginGetsLoadReply_AndLocationsAreStored()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        await tracker.SendAsync(Gt02Frames.Login());
        var reply = await tracker.ReceiveTextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Gt02TextProtocolParser.LoginResponse, reply);

        await tracker.SendAsync(Gt02Frames.Location(60.116, 31.3675, speed: 42, courseDegrees: 120));

        var points = await _server.WaitForPointsAsync(Gt02Frames.Imei, count: 1);

        var point = Assert.Single(points);
        Assert.Equal(60.116, point.Latitude, 4);
        Assert.Equal(31.3675, point.Longitude, 4);
        Assert.Equal(42, point.Speed, 1);
        Assert.Equal(120, point.Course, 1);
    }

    [Fact]
    public async Task Gt02_TimestampIsReceptionTime()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        await tracker.SendAsync(Gt02Frames.Login());
        await tracker.ReceiveTextAsync(TimeSpan.FromSeconds(5));

        var before = DateTime.UtcNow;
        await tracker.SendAsync(Gt02Frames.Location(60.117, 31.368, speed: 50, courseDegrees: 90));

        var points = await _server.WaitForPointsAsync(Gt02Frames.Imei, count: 1);

        var point = Assert.Single(points);
        // Времени суток в GT02 нет — Timestamp должен быть моментом приёма, а не 00:00
        Assert.InRange(point.Timestamp, before, DateTime.UtcNow);
        Assert.InRange(point.CreatedAt, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Gt02_MultipleLocationsAreStoredInOrder()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        await tracker.SendAsync(Gt02Frames.Login());
        await tracker.ReceiveTextAsync(TimeSpan.FromSeconds(5));

        await tracker.SendAsync(Gt02Frames.Location(60.100, 31.300, speed: 10, courseDegrees: 0));
        await tracker.SendAsync(Gt02Frames.Location(60.110, 31.320, speed: 60, courseDegrees: 180));
        await tracker.SendAsync(Gt02Frames.Location(60.120, 31.340, speed: 95, courseDegrees: 359));

        var points = await _server.WaitForPointsAsync(Gt02Frames.Imei, count: 3);

        Assert.Equal(3, points.Count);
        Assert.Equal(60.100, points[0].Latitude, 4);
        Assert.Equal(60.110, points[1].Latitude, 4);
        Assert.Equal(60.120, points[2].Latitude, 4);
    }

    [Fact]
    public async Task Gt02_LocationWithoutLogin_IsNotStored()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        // Локация от неизвестного устройства — IMEI неоткуда взять
        await tracker.SendAsync(Gt02Frames.Location(60.116, 31.3675, speed: 42, courseDegrees: 120));

        var points = await _server.WaitForPointsAsync(Gt02Frames.Imei, count: 1, timeout: TimeSpan.FromSeconds(2));

        Assert.Empty(points);
    }

    [Fact]
    public async Task Gt02_FrameSplitAcrossPackets_IsAssembled()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        // Кадр логина приходит двумя кусками — сервер должен собрать его из буфера
        var login = Gt02Frames.Login();
        await tracker.SendAsync(login[..20]);
        await Task.Delay(150);
        await tracker.SendAsync(login[20..]);

        var reply = await tracker.ReceiveTextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Gt02TextProtocolParser.LoginResponse, reply);
    }

    // ---------------------------------------------------------------- GT06 (бинарный)

    [Fact]
    public async Task Gt06_LoginLocationHeartbeat_GetAcksAndStorePoints()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        var login = Gt06Packets.Login(Gt06Packets.Imei, serial: 1);
        await tracker.SendAsync(login);
        var loginAck = await tracker.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.True(Gt06Packets.IsValidAck(loginAck, 0x01, 1), $"Некорректный ACK на логин: {Convert.ToHexString(loginAck)}");

        var expectedTime = new DateTime(2026, 1, 15, 10, 30, 40, DateTimeKind.Utc);
        var location = Gt06Packets.Location(55.7512, 37.6184, speedKmh: 42, courseDegrees: 120, serial: 2, timeUtc: expectedTime);
        await tracker.SendAsync(location);
        var locationAck = await tracker.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.True(Gt06Packets.IsValidAck(locationAck, 0x12, 2), $"Некорректный ACK на локацию: {Convert.ToHexString(locationAck)}");

        var points = await _server.WaitForPointsAsync(Gt06Packets.Imei, count: 1);
        var point = Assert.Single(points);
        Assert.Equal(55.7512, point.Latitude, 4);
        Assert.Equal(37.6184, point.Longitude, 4);
        Assert.Equal(42, point.Speed, 1);
        Assert.Equal(120, point.Course, 1);
        Assert.Equal(expectedTime, point.Timestamp, TimeSpan.FromSeconds(1));

        await tracker.SendAsync(Gt06Packets.Heartbeat(serial: 3));
        var heartbeatAck = await tracker.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.True(Gt06Packets.IsValidAck(heartbeatAck, 0x23, 3), $"Некорректный ACK на heartbeat: {Convert.ToHexString(heartbeatAck)}");
    }

    [Fact]
    public async Task Gt06_PacketWithCorruptedCrc_IsIgnored()
    {
        using var tracker = new TrackerTestClient();
        await tracker.ConnectAsync(_server.Port);

        await tracker.SendAsync(Gt06Packets.Login(Gt06Packets.Imei, serial: 1));
        await tracker.ReceiveAsync(TimeSpan.FromSeconds(5));

        var corrupted = Gt06Packets.WithCorruptedCrc(
            Gt06Packets.Location(55.7512, 37.6184, speedKmh: 42, courseDegrees: 120, serial: 2));
        await tracker.SendAsync(corrupted);

        var points = await _server.WaitForPointsAsync(Gt06Packets.Imei, count: 1, timeout: TimeSpan.FromSeconds(2));

        Assert.Empty(points);
    }
}
