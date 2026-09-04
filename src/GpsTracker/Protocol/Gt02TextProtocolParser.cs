using System.Globalization;

namespace GpsTracker.Protocol;

/// <summary>
/// Парсер текстового протокола ConCox GT02 — кадры в круглых скобках.
/// Пример кадра логина (BP):
/// (027046781654BP05355227046781654260903A6006.9472N03122.7910E120.0224043081.9901000000L00000000)
/// Пример кадра локации (BR):
/// (027046781654BR00260903A6006.9472N03122.7910E120.0224058241.2301000000L00000000)
///
/// Структура кадра: 12 цифр ID устройства, команда (BP — логин, BR — локация, BA — тревога),
/// 2 цифры подтипа, у логина дополнительно IMEI (15 цифр), далее общая часть:
/// дата YYMMDD, признак фикса A/V, широта ddmm.mmmm, N/S, долгота dddmm.mmmm, E/W,
/// скорость XXX.XX км/ч, курс XXXXX (градусы × 100), служебные поля и пробег LXXXXXXXX.
/// Времени суток в кадре нет — временем точки считается момент приёма (DateTime.UtcNow),
/// дата из кадра используется только как проверка целостности кадра.
/// </summary>
public static class Gt02TextProtocolParser
{
    /// <summary>Ответ сервера на кадр логина — после него трекер начинает слать локации.</summary>
    public const string LoginResponse = "LOAD";

    private const int DeviceIdLength = 12;
    private const int CommandLength = 2;
    private const int SubtypeLength = 2;
    private const int ImeiLength = 15;       // стандартная длина IMEI
    private const int DateLength = 6;
    private const int FixFlagLength = 1;
    private const int LatitudeLength = 9;    // ddmm.mmmm
    private const int LongitudeLength = 10;  // dddmm.mmmm
    private const int HemisphereLength = 1;
    private const int SpeedLength = 6;       // XXX.XX
    private const int CourseLength = 5;      // XXXXX = градусы × 100

    private const int HeaderLength = DeviceIdLength + CommandLength + SubtypeLength;

    public static List<Gt06Packet> Parse(string data)
    {
        var packets = new List<Gt06Packet>();

        foreach (var frame in ExtractFrames(data))
        {
            try
            {
                var packet = ParseFrame(frame);
                if (packet is not null)
                {
                    packets.Add(packet);
                }
            }
            catch (FormatException)
            {
                // Некорректный кадр пропускаем: в потоке могут быть и валидные
            }
        }

        return packets;
    }

    /// <summary>Извлекает содержимое всех полных кадров "( ... )" из потока данных.</summary>
    internal static IEnumerable<string> ExtractFrames(string data)
    {
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '(')
            {
                start = i;
            }
            else if (data[i] == ')' && start >= 0)
            {
                if (i - start > 1)
                {
                    yield return data[(start + 1)..i];
                }
                start = -1;
            }
        }
    }

    private static Gt06Packet? ParseFrame(string frame)
    {
        if (frame.Length < HeaderLength || !IsDigits(frame, 0, DeviceIdLength))
        {
            return null;
        }

        int pos = DeviceIdLength;
        var command = frame.Substring(pos, CommandLength);
        pos += CommandLength + SubtypeLength;

        var packet = new Gt06Packet();

        switch (command)
        {
            case "BP": // логин
                if (frame.Length < pos + ImeiLength || !IsDigits(frame, pos, ImeiLength))
                {
                    return null;
                }
                packet.Type = Gt06PacketType.Login;
                packet.Imei = frame.Substring(pos, ImeiLength);
                pos += ImeiLength;
                break;

            case "BR": // обычный отчёт о локации
                packet.Type = Gt06PacketType.Location;
                break;

            case "BA": // тревога — формат локации совпадает с BR
                packet.Type = Gt06PacketType.Alarm;
                break;

            default:
                packet.Type = Gt06PacketType.Unknown;
                return packet;
        }

        // Общая часть после (для BP — после IMEI): дата YYMMDD, фикс A/V, координаты и т.д.
        int requiredLength = pos
                             + DateLength + FixFlagLength
                             + LatitudeLength + HemisphereLength
                             + LongitudeLength + HemisphereLength
                             + SpeedLength + CourseLength;
        if (frame.Length < requiredLength)
        {
            return null;
        }

        int yy = ParseInt(frame, pos, 2); pos += 2;
        int mm = ParseInt(frame, pos, 2); pos += 2;
        int dd = ParseInt(frame, pos, 2); pos += 2;

        // Дата из кадра используется только как проверка целостности:
        // времени суток в GT02 нет, поэтому временем точки считается момент приёма
        if (!IsValidDate(2000 + yy, mm, dd))
        {
            return null;
        }
        packet.Timestamp = DateTime.UtcNow;

        packet.IsValidGps = char.ToUpperInvariant(frame[pos]) == 'A';
        pos += FixFlagLength;

        double latRaw = ParseDouble(frame, pos, LatitudeLength); pos += LatitudeLength;
        var latSign = char.ToUpperInvariant(frame[pos]) == 'S' ? -1 : 1; pos += HemisphereLength;

        double lonRaw = ParseDouble(frame, pos, LongitudeLength); pos += LongitudeLength;
        var lonSign = char.ToUpperInvariant(frame[pos]) == 'W' ? -1 : 1; pos += HemisphereLength;

        packet.Latitude = DegreesMinutesToDegrees(latRaw) * latSign;
        packet.Longitude = DegreesMinutesToDegrees(lonRaw) * lonSign;
        packet.Speed = ParseDouble(frame, pos, SpeedLength); pos += SpeedLength;
        packet.Course = ParseInt(frame, pos, CourseLength) / 100.0;

        return packet;
    }

    /// <summary>Преобразует значение в формате ddmm.mmmm (или dddmm.mmmm) в десятичные градусы.</summary>
    private static double DegreesMinutesToDegrees(double value)
    {
        int degrees = (int)(value / 100);
        double minutes = value - degrees * 100;
        return degrees + minutes / 60.0;
    }

    private static bool IsValidDate(int year, int month, int day)
    {
        try
        {
            _ = new DateTime(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsDigits(string s, int offset, int length)
    {
        for (int i = offset; i < offset + length; i++)
        {
            if (s[i] is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static int ParseInt(string s, int offset, int length)
    {
        return int.Parse(s.AsSpan(offset, length), CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string s, int offset, int length)
    {
        return double.Parse(s.AsSpan(offset, length), CultureInfo.InvariantCulture);
    }
}
