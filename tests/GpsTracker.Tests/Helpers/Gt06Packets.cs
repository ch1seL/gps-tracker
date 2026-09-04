using System.Buffers.Binary;

namespace GpsTracker.Tests.Helpers;

/// <summary>
/// Конструктор пакетов бинарного протокола GT06 — аналог режима gt06 симулятора:
/// [0x78 0x78][len][proto][body][crc_hi][crc_lo][0x0D 0x0A],
/// CRC-16/IBM от байта длины до последнего байта данных.
/// </summary>
internal static class Gt06Packets
{
    public const string Imei = "8681201234567890";

    public static byte[] Login(string imei, ushort serial)
    {
        var body = new List<byte> { 0x01, 0x02 }; // инфо-байты формата ConCox GT02
        body.AddRange(Convert.FromHexString(imei[..16]));
        body.AddRange(Be16(serial));
        return MakePacket(0x01, body.ToArray());
    }

    public static byte[] Location(
        double lat, double lon, int speedKmh, int courseDegrees, ushort serial, DateTime? timeUtc = null)
    {
        var time = timeUtc ?? DateTime.UtcNow;

        var body = new List<byte>(20);
        body.AddRange(BcdTime(time));
        body.Add(0x80 | 10); // фикс + 10 спутников
        body.AddRange(Be32((uint)Math.Round(Math.Abs(lat) * 60 * 30000)));
        body.AddRange(Be32((uint)Math.Round(Math.Abs(lon) * 60 * 30000)));
        body.Add((byte)Math.Clamp(speedKmh, 0, 255));
        body.AddRange(Be16((ushort)(HemisphereFlags(lat, lon) | (courseDegrees & 0x3FF))));
        body.AddRange(Be16(serial));
        return MakePacket(0x12, body.ToArray());
    }

    public static byte[] Heartbeat(ushort serial)
    {
        // 0x0001 0x0004 + информация терминала (4) + напряжение (2) + сигнал GSM (1) + серийник
        var body = new byte[] { 0x00, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x40 };
        var full = new List<byte>(body) { (byte)(serial >> 8), (byte)(serial & 0xFF) };
        return MakePacket(0x23, full.ToArray());
    }

    /// <summary>Возвращает копию пакета с инвертированным старшим байтом CRC — для теста отбраковки.</summary>
    public static byte[] WithCorruptedCrc(byte[] packet)
    {
        var copy = (byte[])packet.Clone();
        copy[^4] ^= 0xFF;
        return copy;
    }

    /// <summary>Проверяет структуру ответа сервера (ACK) и корректность его CRC.</summary>
    public static bool IsValidAck(byte[] ack, byte expectedProtocol, ushort expectedSerial)
    {
        return ack.Length == 10
               && ack[0] == 0x78 && ack[1] == 0x78
               && ack[2] == 0x05
               && ack[3] == expectedProtocol
               && ack[4] == (byte)(expectedSerial >> 8)
               && ack[5] == (byte)(expectedSerial & 0xFF)
               && ack[8] == 0x0D && ack[9] == 0x0A
               && Crc16Ibm(ack.AsSpan(2, 4)) == BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(6));
    }

    private static ushort HemisphereFlags(double lat, double lon)
    {
        var flags = 0;
        if (lat < 0) flags |= 0x0400; // юг
        if (lon < 0) flags |= 0x0800; // запад
        return (ushort)flags;
    }

    private static byte[] MakePacket(byte protocol, byte[] body)
    {
        // length = protocol(1) + body + crc(2)
        var length = 1 + body.Length + 2;
        var packet = new byte[2 + 1 + length + 2]; // header(2) + len(1) + proto+body + crc(2) + tail(2)
        packet[0] = 0x78;
        packet[1] = 0x78;
        packet[2] = (byte)length;
        packet[3] = protocol;
        body.CopyTo(packet, 4);

        // CRC считается от байта длины до последнего байта данных включительно
        var crc = Crc16Ibm(packet.AsSpan(2, 2 + body.Length));
        packet[4 + body.Length] = (byte)(crc >> 8);
        packet[5 + body.Length] = (byte)(crc & 0xFF);
        packet[^2] = 0x0D;
        packet[^1] = 0x0A;
        return packet;
    }

    private static byte[] BcdTime(DateTime t) => new[]
    {
        Bcd(t.Year % 100), Bcd(t.Month), Bcd(t.Day), Bcd(t.Hour), Bcd(t.Minute), Bcd(t.Second)
    };

    private static byte Bcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static byte[] Be16(ushort value) => new[] { (byte)(value >> 8), (byte)(value & 0xFF) };

    private static byte[] Be32(uint value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    };

    private static ushort Crc16Ibm(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }
}
