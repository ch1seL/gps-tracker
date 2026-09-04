using System.Globalization;

namespace GpsTracker.Tests.Helpers;

/// <summary>
/// Конструктор кадров текстового протокола ConCox GT02 — аналог режима gt02 симулятора.
/// Формат соответствует реальной выгрузке трекера (см. Gt02TextProtocolParser).
/// </summary>
internal static class Gt02Frames
{
    public const string DeviceId = "027046781654";
    public const string Imei = "355227046781654";

    private static string Today() =>
        DateTime.UtcNow.ToString("yyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Кадр логина (BP): ID, команда, IMEI, дата, фикс, координаты, служебные поля.</summary>
    public static string Login(string? date = null) =>
        $"({DeviceId}BP05{Imei}{date ?? Today()}A" +
        $"{Dm(60.1158, latitude: true)}N{Dm(31.3798, latitude: false)}E" +
        $"120.0224043081.9901000000L00000000)";

    /// <summary>Кадр локации (BR): без IMEI — он привязывается к сессии на сервере.</summary>
    public static string Location(
        double lat, double lon, double speed, int courseDegrees, string? date = null) =>
        $"({DeviceId}BR00{date ?? Today()}A" +
        $"{Dm(lat, latitude: true)}N{Dm(lon, latitude: false)}E" +
        $"{speed.ToString("000.00", CultureInfo.InvariantCulture)}" +
        $"{(courseDegrees * 100).ToString("D5", CultureInfo.InvariantCulture)}" +
        $"0000000000L00000000)";

    /// <summary>Десятичные градусы → ddmm.mmmm (широта) или dddmm.mmmm (долгота).</summary>
    private static string Dm(double value, bool latitude)
    {
        var degrees = (int)Math.Abs(value);
        var minutes = (Math.Abs(value) - degrees) * 60;
        // Важно: InvariantCulture, иначе в ru-RU получится "06,0000" с запятой
        return latitude
            ? $"{degrees:D2}{minutes.ToString("00.0000", CultureInfo.InvariantCulture)}"
            : $"{degrees:D3}{minutes.ToString("00.0000", CultureInfo.InvariantCulture)}";
    }
}
