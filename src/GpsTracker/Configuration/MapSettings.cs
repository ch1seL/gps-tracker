namespace GpsTracker.Configuration;

public class MapSettings
{
    public const string SectionName = "Map";

    /// <summary>
    /// Шаблон URL тайл-сервера, например https://tile.openstreetmap.org/{z}/{x}/{y}.png
    /// </summary>
    public string TileServerUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    public int DefaultZoom { get; set; } = 15;

    public string CachePath { get; set; } = "./tile-cache";

    /// <summary>
    /// Пользовательский User-Agent — обязателен при использовании тайлов OSM.
    /// </summary>
    public string UserAgent { get; set; } = "GpsTracker/1.0";
}
