using GpsTracker.Protocol;
using Xunit;

namespace GpsTracker.Tests;

/// <summary>
/// Тесты на реальных кадрах трекера ConCox GT02 (текстовый протокол),
/// снятых через nc -l 5023.
/// </summary>
public class Gt02TextProtocolParserTests
{
    // Кадры из реальной выгрузки пользователя (nc -l 5023)
    private const string LoginFrame =
        "(027046781654BP05355227046781654260903A6006.9472N03122.7910E120.0224043081.9901000000L00000000)";

    private const string LocationFrame1 =
        "(027046781654BR00260903A6006.9613N03122.0476E116.1224058241.2301000000L00000000)";

    private const string LocationFrame2 =
        "(027046781654BR00260903A6007.0020N03121.8238E049.5224113277.0201000000L00000000)";

    [Fact]
    public void Parse_LoginFrame_ReturnsLoginWithImei()
    {
        var packets = Gt02TextProtocolParser.Parse(LoginFrame);

        var packet = Assert.Single(packets);
        Assert.Equal(Gt06PacketType.Login, packet.Type);
        Assert.Equal("355227046781654", packet.Imei);
    }

    [Theory]
    // 6006.9613N = 60 + 6.9613/60 = 60.11602; 03122.0476E = 31 + 22.0476/60 = 31.36746;
    // курс "24058" = 240.58°
    [InlineData(LocationFrame1, 60.11602, 31.36746, 116.12, 240.58)]
    // 6007.0020N = 60.11670; 03121.8238E = 31.36373; курс "24113" = 241.13°
    [InlineData(LocationFrame2, 60.1167, 31.36373, 49.52, 241.13)]
    public void Parse_LocationFrame_ParsesCoordinatesSpeedCourse(
        string frame, double lat, double lon, double speed, double course)
    {
        var packets = Gt02TextProtocolParser.Parse(frame);

        var packet = Assert.Single(packets);
        Assert.Equal(Gt06PacketType.Location, packet.Type);
        Assert.Equal(lat, packet.Latitude, 4);
        Assert.Equal(lon, packet.Longitude, 4);
        Assert.Equal(speed, packet.Speed, 2);
        Assert.Equal(course, packet.Course, 2);
        Assert.True(packet.IsValidGps);
    }

    [Fact]
    public void Parse_LocationFrame_UsesReceptionTimeAsTimestamp()
    {
        var before = DateTime.UtcNow;
        var packets = Gt02TextProtocolParser.Parse(LocationFrame1);
        var after = DateTime.UtcNow;

        var packet = Assert.Single(packets);
        // Времени суток в GT02 нет — временем точки считается момент приёма
        Assert.InRange(packet.Timestamp, before, after);
    }

    [Fact]
    public void Parse_FrameWithInvalidDate_ReturnsNothing()
    {
        // Месяц 99 некорректен — кадр отбрасывается
        var frame = "(027046781654BR00999903A6006.9472N03122.7910E120.0224043081.9901000000L00000000)";

        var packets = Gt02TextProtocolParser.Parse(frame);

        Assert.Empty(packets);
    }

    [Fact]
    public void Parse_MultipleFramesInOneChunk_ReturnsAllPackets()
    {
        var data = LoginFrame + LocationFrame1 + LocationFrame2;

        var packets = Gt02TextProtocolParser.Parse(data);

        Assert.Equal(3, packets.Count);
        Assert.Equal(Gt06PacketType.Login, packets[0].Type);
        Assert.Equal(Gt06PacketType.Location, packets[1].Type);
        Assert.Equal(Gt06PacketType.Location, packets[2].Type);
        // IMEI есть только в кадре логина; в кадрах локации он привязывается на уровне сессии
        Assert.Equal("355227046781654", packets[0].Imei);
        Assert.Equal(string.Empty, packets[1].Imei);
    }

    [Fact]
    public void Parse_IncompleteFrame_ReturnsNothing()
    {
        // Обрезанный кадр без закрывающей скобки
        var partial = LoginFrame[..^10];

        var packets = Gt02TextProtocolParser.Parse(partial);

        Assert.Empty(packets);
    }

    [Fact]
    public void Parse_GarbageBetweenFrames_SkipsIt()
    {
        var data = "junk-prefix" + LoginFrame + "\r\n" + LocationFrame1;

        var packets = Gt02TextProtocolParser.Parse(data);

        Assert.Equal(2, packets.Count);
    }

    [Fact]
    public void Parse_SouthWestCoordinates_AreNegative()
    {
        // 6006.9472S = -60.1158; 03122.7910W = -31.3798 (float-представление 31.3797999…)
        var frame = "(027046781654BR00260903A6006.9472S03122.7910W120.0224043081.9901000000L00000000)";

        var packet = Assert.Single(Gt02TextProtocolParser.Parse(frame));

        Assert.Equal(-60.1158, packet.Latitude, 4);
        Assert.Equal(-31.3798, packet.Longitude, 4);
    }

    [Fact]
    public void Parse_NoFixFlag_MarksInvalidGps()
    {
        // V = нет фикса
        var frame = "(027046781654BR00260903V6006.9472N03122.7910E000.000000000.0001000000L00000000)";

        var packet = Assert.Single(Gt02TextProtocolParser.Parse(frame));

        Assert.False(packet.IsValidGps);
    }

    [Fact]
    public void Parse_ArmFrame_IsUnknownType()
    {
        // BA — тревога; проверяем кадр с неизвестной командой ZZ
        var frame = "(027046781654ZZ00260903A6006.9472N03122.7910E120.0224043081.9901000000L00000000)";

        var packet = Assert.Single(Gt02TextProtocolParser.Parse(frame));

        Assert.Equal(Gt06PacketType.Unknown, packet.Type);
    }
}
