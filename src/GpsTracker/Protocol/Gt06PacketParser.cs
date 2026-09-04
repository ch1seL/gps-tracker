using System.Buffers.Binary;
using System.Text;

namespace GpsTracker.Protocol;

public static class Gt06PacketParser
{
    public static List<Gt06Packet> Parse(byte[] buffer, int offset, int length)
    {
        var packets = new List<Gt06Packet>();
        var pos = offset;
        var end = offset + length;

        while (pos < end - 1)
        {
            // Search for header 0x78 0x78
            if (buffer[pos] == Gt06ProtocolConstants.HeaderHigh &&
                buffer[pos + 1] == Gt06ProtocolConstants.HeaderLow)
            {
                // Minimum packet: 0x78 0x78 len proto ... crc crc 0x0D 0x0A = 10 bytes
                if (pos + 3 >= end) break;

                int packetLength = buffer[pos + 2];

                var totalLength = // header(2) + len(1) + data(len) + crc(2) + tail(1) ... actually: 2(header) + 1(length) + (length-4) data + 2(crc) + 2(tail)
                    // Actually GT06 format: [0x78][0x78][Length][Protocol][Data...][CRC_H][CRC_L][0x0D][0x0A]
                    // Length = number of bytes from Protocol to CRC_L inclusive
                    // So total packet = 2(header) + 1(length) + Length + 2(tail)
                    2 + 1 + packetLength + 2;

                if (pos + totalLength > end) break; // Not enough data yet

                var packetBytes = buffer[pos..(pos + totalLength)];

                // Verify CRC - CRC is calculated from Length byte to last data byte (excluding CRC itself)
                var crcDataStart = pos + 2; // length byte
                var crcDataEnd = pos + totalLength - 4; // before CRC (2 bytes) and tail (0x0D 0x0A)
                var expectedCrc = Crc16(buffer, crcDataStart, crcDataEnd - crcDataStart);
                var actualCrc = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(pos + totalLength - 4));

                if (expectedCrc != actualCrc)
                {
                    pos++;
                    continue;
                }

                var packet = ParsePacket(packetBytes);
                packets.Add(packet);

                pos += totalLength;
            }
            else
            {
                pos++;
            }
        }

        return packets;
    }

    private static Gt06Packet ParsePacket(byte[] data)
    {
        // data[0] = 0x78, data[1] = 0x78, data[2] = length, data[3] = protocol
        var protocolNumber = data[3];

        var packet = new Gt06Packet
        {
            ProtocolNumber = protocolNumber,
            SerialNumber = (ushort)((data[data.Length - 6] << 8) | data[data.Length - 5]),
            RawData = data
        };

        switch (protocolNumber)
        {
            case Gt06ProtocolConstants.Login:
                return ParseLoginPacket(data, packet);
            case Gt06ProtocolConstants.Location:
            case Gt06ProtocolConstants.Alarm:
                return ParseLocationPacket(data, packet);
            case Gt06ProtocolConstants.Status:
                return ParseStatusPacket(data, packet);
            case Gt06ProtocolConstants.Heartbeat:
                return ParseHeartbeatPacket(data, packet);
            default:
                packet.Type = Gt06PacketType.Unknown;
                return packet;
        }
    }

    private static Gt06Packet ParseLoginPacket(byte[] data, Gt06Packet packet)
    {
        packet.Type = Gt06PacketType.Login;

        // ConCox GT02: [0x78 0x78][len][0x01][info 2 байта][IMEI 8 байт BCD][serial 2][crc 2]
        // Длина пакета = protocol(1) + info(2) + imei(8) + serial(2) + crc(2) = 15.
        // Часть прошивок шлёт без info-байтов (len = 13) — различаем по длине.
        var imeiStart = data[2] >= 15 ? 6 : 4;
        var imeiBytes = data[imeiStart..(imeiStart + 8)];
        packet.Imei = BcdToString(imeiBytes);

        return packet;
    }

    private static Gt06Packet ParseLocationPacket(byte[] data, Gt06Packet packet)
    {
        packet.Type = packet.ProtocolNumber == Gt06ProtocolConstants.Alarm
            ? Gt06PacketType.Alarm
            : Gt06PacketType.Location;

        // Data starts at byte 4
        // Byte 4: Date/Time (6 bytes: YY MM DD HH MM SS)
        var year = 2000 + BcdToByte(data[4]);
        int month = BcdToByte(data[5]);
        int day = BcdToByte(data[6]);
        int hour = BcdToByte(data[7]);
        int minute = BcdToByte(data[8]);
        int second = BcdToByte(data[9]);

        try
        {
            packet.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch
        {
            packet.Timestamp = DateTime.UtcNow;
        }

        // Byte 10: GPS info byte
        // Bit 7: GPS fix (1 = valid)
        // Bits 0-3: number of satellites
        var gpsInfo = data[10];
        packet.IsValidGps = (gpsInfo & 0x10) != 0; // Actually bit 4 for some firmware, bit 7 for others
        if (!packet.IsValidGps)
            packet.IsValidGps = (gpsInfo & 0x80) != 0;
        packet.Satellites = gpsInfo & 0x0F;

        // Bytes 11-14: Latitude (4 bytes, in minutes * 30000, big-endian)
        var latRaw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(11));
        packet.Latitude = latRaw / 1800000.0;

        // Bytes 15-18: Longitude (4 bytes, in minutes * 30000, big-endian)
        var lonRaw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(15));
        packet.Longitude = lonRaw / 1800000.0;

        // Byte 19-20: Speed (1 byte in km/h)
        packet.Speed = data[19];

        // Bytes 20-21: Course/Status (2 bytes)
        var courseStatus = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20));
        // Bit 10: East/West longitude (0 = East, 1 = West)
        // Bit 11: North/South latitude (0 = North, 1 = South)
        // Bits 0-9: course (0-359)
        packet.Course = courseStatus & 0x03FF;

        if ((courseStatus & 0x0400) != 0) // West
            packet.Longitude = -packet.Longitude;
        if ((courseStatus & 0x0800) != 0) // South
            packet.Latitude = -packet.Latitude;

        return packet;
    }

    private static Gt06Packet ParseStatusPacket(byte[] data, Gt06Packet packet)
    {
        packet.Type = Gt06PacketType.Status;

        // Status packet contains: date/time (6 bytes), info byte, voltage, GPS accuracy, speed
        if (data.Length > 10)
        {
            var year = 2000 + BcdToByte(data[4]);
            int month = BcdToByte(data[5]);
            int day = BcdToByte(data[6]);
            int hour = BcdToByte(data[7]);
            int minute = BcdToByte(data[8]);
            int second = BcdToByte(data[9]);

            try
            {
                packet.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            }
            catch
            {
                packet.Timestamp = DateTime.UtcNow;
            }
        }

        return packet;
    }

    private static Gt06Packet ParseHeartbeatPacket(byte[] data, Gt06Packet packet)
    {
        packet.Type = Gt06PacketType.Heartbeat;

        if (data.Length > 9)
        {
            var year = 2000 + BcdToByte(data[4]);
            int month = BcdToByte(data[5]);
            int day = BcdToByte(data[6]);
            int hour = BcdToByte(data[7]);
            int minute = BcdToByte(data[8]);
            int second = BcdToByte(data[9]);

            try
            {
                packet.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            }
            catch
            {
                packet.Timestamp = DateTime.UtcNow;
            }
        }

        return packet;
    }

    public static byte[] CreateLoginResponse(ushort serialNumber)
    {
        return CreateResponse(Gt06ProtocolConstants.LoginResponse, serialNumber);
    }

    public static byte[] CreateLocationResponse(ushort serialNumber)
    {
        return CreateResponse(Gt06ProtocolConstants.LocationResponse, serialNumber);
    }

    public static byte[] CreateHeartbeatResponse(ushort serialNumber)
    {
        return CreateResponse(Gt06ProtocolConstants.HeartbeatResponse, serialNumber);
    }

    private static byte[] CreateResponse(byte protocolNumber, ushort serialNumber)
    {
        // Response: 0x78 0x78 Length Protocol SerialNumber CRC CRC 0x0D 0x0A
        // Length = 1 (protocol) + 2 (serial) + 2 (CRC) = 5
        var response = new byte[10];
        response[0] = Gt06ProtocolConstants.HeaderHigh;
        response[1] = Gt06ProtocolConstants.HeaderLow;
        response[2] = 0x05; // length
        response[3] = protocolNumber;
        response[4] = (byte)(serialNumber >> 8);
        response[5] = (byte)(serialNumber & 0xFF);

        var crc = Crc16(response, 2, 4); // from length byte to last data byte
        response[6] = (byte)(crc >> 8);
        response[7] = (byte)(crc & 0xFF);
        response[8] = 0x0D;
        response[9] = 0x0A;

        return response;
    }

    private static ushort Crc16(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (var i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (var j = 0; j < 8; j++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                else
                    crc >>= 1;
            }
        }
        return crc;
    }

    private static byte BcdToByte(byte bcd)
    {
        return (byte)((bcd >> 4) * 10 + (bcd & 0x0F));
    }

    private static string BcdToString(byte[] bcd)
    {
        var sb = new StringBuilder(bcd.Length * 2);
        foreach (var b in bcd)
        {
            sb.Append((b >> 4) & 0x0F);
            sb.Append(b & 0x0F);
        }
        // IMEI — идентификатор фиксированной длины: ведущие нули значимы
        return sb.ToString();
    }
}
