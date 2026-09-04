using GpsTracker.Configuration;
using GpsTracker.Models;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace GpsTracker.Services;

public class MapRendererService : IMapRendererService
{
    private const int TileSize = 256;

    private readonly MapSettings _mapSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MapRendererService> _logger;

    public MapRendererService(
        IOptions<MapSettings> mapSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<MapRendererService> logger)
    {
        _mapSettings = mapSettings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Рисует карту с одним маркером (текущая позиция).
    /// </summary>
    public async Task<byte[]> RenderPositionAsync(GpsPoint point, CancellationToken ct = default)
    {
        var zoom = Math.Clamp(_mapSettings.DefaultZoom, 1, 18);

        var tiles = TileGrid.Calculate(
            new[] { new GeoPoint(point.Latitude, point.Longitude) },
            zoom,
            imageSize: 512,
            paddingPx: 96);

        using var surface = SKSurface.Create(new SKImageInfo(tiles.Width, tiles.Height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xE8, 0xE6, 0xE1));

        await DrawTilesAsync(canvas, tiles, ct);

        var px = tiles.Project(point.Latitude, point.Longitude);
        DrawMarker(canvas, px.X, px.Y);

        return EncodePng(surface);
    }

    /// <summary>
    /// Рисует карту истории движения: линия каждого сегмента окрашена по скорости.
    /// </summary>
    public async Task<byte[]> RenderTrackAsync(IReadOnlyList<GpsPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0)
        {
            throw new ArgumentException("Нет точек для отрисовки", nameof(points));
        }

        var ordered = points.OrderBy(p => p.Timestamp).ToList();
        var imageSize = Math.Max(512, Math.Min(1024, ordered.Count * 2 + 256));
        var geoPoints = ordered.Select(p => new GeoPoint(p.Latitude, p.Longitude)).ToList();

        var zoom = TileGrid.ChooseZoom(geoPoints, imageSize, paddingPx: 48);
        var tiles = TileGrid.Calculate(geoPoints, zoom, imageSize, paddingPx: 48);

        using var surface = SKSurface.Create(new SKImageInfo(tiles.Width, tiles.Height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xE8, 0xE6, 0xE1));

        await DrawTilesAsync(canvas, tiles, ct);
        DrawSpeedTrack(canvas, tiles, ordered);

        return EncodePng(surface);
    }

    // ---------- Отрисовка ----------

    private async Task DrawTilesAsync(SKCanvas canvas, TileGrid tiles, CancellationToken ct)
    {
        var httpClient = _httpClientFactory.CreateClient("tiles");
        var semaphore = new SemaphoreSlim(4);

        var tasks = tiles.Coordinates()
            .Select(async tile =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var bytes = await LoadTileAsync(httpClient, tile, ct);
                    if (bytes is null) return;

                    using var bitmap = SKBitmap.Decode(bytes);
                    if (bitmap is null) return;

                    // Origin дробный: тайлы рисуются с субпиксельным сдвигом,
                    // лишнее обрезается краями канвы; Scale < 1 — тайлы сжимаются
                    // вместе с проекцией
                    var tilePx = (float)(TileSize * tiles.Scale);
                    canvas.DrawBitmap(
                        bitmap,
                        new SKRect(
                            (float)((tile.X - tiles.OriginX) * tilePx),
                            (float)((tile.Y - tiles.OriginY) * tilePx),
                            (float)((tile.X - tiles.OriginX + 1) * tilePx),
                            (float)((tile.Y - tiles.OriginY + 1) * tilePx)),
                        new SKSamplingOptions(SKCubicResampler.Mitchell));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось загрузить тайл {Zoom}/{X}/{Y}", tile.Z, tile.X, tile.Y);
                }
                finally
                {
                    semaphore.Release();
                }
            });

        await Task.WhenAll(tasks);
    }

    private async Task<byte[]?> LoadTileAsync(HttpClient httpClient, TileCoordinate tile, CancellationToken ct)
    {
        // Файловый кэш: повторные запросы не обращаются к тайл-серверу
        var cacheFile = Path.Combine(
            _mapSettings.CachePath,
            tile.Z.ToString(),
            tile.X.ToString(),
            $"{tile.Y}.png");

        if (File.Exists(cacheFile))
        {
            return await File.ReadAllBytesAsync(cacheFile, ct);
        }

        var url = _mapSettings.TileServerUrl
            .Replace("{z}", tile.Z.ToString())
            .Replace("{x}", tile.X.ToString())
            .Replace("{y}", tile.Y.ToString());

        var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Тайл-сервер вернул {StatusCode} для {Url}", response.StatusCode, url);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        await File.WriteAllBytesAsync(cacheFile, bytes, ct);

        return bytes;
    }

    private static void DrawSpeedTrack(SKCanvas canvas, TileGrid tiles, IReadOnlyList<GpsPoint> ordered)
    {
        if (ordered.Count < 2) return;

        var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        // Отдельный сегмент между соседними точками, цвет — по средней скорости сегмента
        for (var i = 1; i < ordered.Count; i++)
        {
            var a = tiles.Project(ordered[i - 1].Latitude, ordered[i - 1].Longitude);
            var b = tiles.Project(ordered[i].Latitude, ordered[i].Longitude);

            paint.Color = SpeedColor((ordered[i - 1].Speed + ordered[i].Speed) / 2);
            canvas.DrawLine(a.X, a.Y, b.X, b.Y, paint);
        }

        // Начало и конец маршрута
        var first = tiles.Project(ordered[0].Latitude, ordered[0].Longitude);
        var last = tiles.Project(ordered[^1].Latitude, ordered[^1].Longitude);
        DrawMarker(canvas, last.X, last.Y);
        DrawStartMarker(canvas, first.X, first.Y);
    }

    /// <summary>Цвет линии по скорости: зелёный → жёлтый → оранжевый → красный.</summary>
    public static SKColor SpeedColor(double speedKmh)
    {
        return speedKmh switch
        {
            < 30 => new SKColor(0x4C, 0xAF, 0x50),   // зелёный
            < 60 => new SKColor(0xFF, 0xEB, 0x3B),   // жёлтый
            < 90 => new SKColor(0xFF, 0x98, 0x00),   // оранжевый
            _ => new SKColor(0xF4, 0x43, 0x36)       // красный
        };
    }

    private static void DrawMarker(SKCanvas canvas, float x, float y)
    {
        const float radius = 9f;

        using var white = new SKPaint();
        white.Color = SKColors.White;
        white.IsAntialias = true;
        using var red = new SKPaint();
        red.Color = new SKColor(0xE5, 0x39, 0x35);
        red.IsAntialias = true;
        using var shadow = new SKPaint();
        shadow.Color = new SKColor(0, 0, 0, 60);
        shadow.IsAntialias = true;
        shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);

        canvas.DrawCircle(x, y, radius + 2, shadow);
        canvas.DrawCircle(x, y, radius, white);
        canvas.DrawCircle(x, y, radius - 3, red);
    }

    private static void DrawStartMarker(SKCanvas canvas, float x, float y)
    {
        using var paint = new SKPaint();
        paint.Color = new SKColor(0x1E, 0x88, 0xE5);
        paint.IsAntialias = true;
        using var border = new SKPaint();
        border.Color = SKColors.White;
        border.Style = SKPaintStyle.Stroke;
        border.StrokeWidth = 2;
        canvas.DrawCircle(x, y, 7, paint);
        canvas.DrawCircle(x, y, 7, border);
    }

    private static byte[] EncodePng(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}

// ---------- Вспомогательная геометрия Web Mercator ----------

public interface IMapRendererService
{
    Task<byte[]> RenderPositionAsync(GpsPoint point, CancellationToken ct = default);
    Task<byte[]> RenderTrackAsync(IReadOnlyList<GpsPoint> points, CancellationToken ct = default);
}

public readonly record struct GeoPoint(double Latitude, double Longitude);

public readonly record struct TileCoordinate(int Z, int X, int Y);

public readonly record struct CanvasPoint(float X, float Y);

/// <summary>
/// Раскладка тайлов Web Mercator: какие тайлы нужны и как проецировать координаты на канву.
/// Начало сетки (Origin) — дробное в тайловых координатах: это позволяет положить любую
/// точку точно в заданное место канвы (например, в центр), рисуя тайлы с субпиксельным
/// сдвигом и обрезая их краями изображения.
/// </summary>
public class TileGrid
{
    private const double TileSizePx = 256;

    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    public int Zoom { get; }
    public int MinX { get; }
    public int MaxX { get; }
    public int MinY { get; }
    public int MaxY { get; }
    public double Scale { get; } // пикселей канвы на пиксель тайла

    /// <summary>Начало канвы в тайловых координатах (дробное; по умолчанию MinX/MinY).</summary>
    public double OriginX { get; }
    public double OriginY { get; }

    public TileGrid(
        int zoom,
        int minX, int maxX, int minY, int maxY,
        double scale,
        double? originX = null,
        double? originY = null,
        int? canvasWidth = null,
        int? canvasHeight = null)
    {
        Zoom = zoom;
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
        Scale = scale;
        OriginX = originX ?? minX;
        OriginY = originY ?? minY;

        // Канва по умолчанию = вся площадь тайлов (целочисленная раскладка)
        _canvasWidth = canvasWidth ?? (int)Math.Ceiling((MaxX - MinX + 1) * TileSizePx * Scale);
        _canvasHeight = canvasHeight ?? (int)Math.Ceiling((MaxY - MinY + 1) * TileSizePx * Scale);
    }

    public int Width => _canvasWidth;
    public int Height => _canvasHeight;

    public static double LatitudeToY(double latitude, int zoom)
    {
        var latRad = latitude * Math.PI / 180.0;
        var n = Math.Pow(2, zoom);
        return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
    }

    public static double LongitudeToX(double longitude, int zoom)
    {
        var n = Math.Pow(2, zoom);
        return (longitude + 180.0) / 360.0 * n;
    }

    public CanvasPoint Project(double latitude, double longitude)
    {
        var x = LongitudeToX(longitude, Zoom);
        var y = LatitudeToY(latitude, Zoom);
        return new CanvasPoint(
            (float)((x - OriginX) * TileSizePx * Scale),
            (float)((y - OriginY) * TileSizePx * Scale));
    }

    public IEnumerable<TileCoordinate> Coordinates()
    {
        for (var y = MinY; y <= MaxY; y++)
        {
            for (var x = MinX; x <= MaxX; x++)
            {
                yield return new TileCoordinate(Zoom, x, y);
            }
        }
    }

    /// <summary>
    /// Считает раскладку тайлов для набора точек: центр bounding box попадает
    /// точно в центр канвы независимо от выравнивания тайловой сетки.
    /// </summary>
    public static TileGrid Calculate(
        IReadOnlyList<GeoPoint> points,
        int zoom,
        int imageSize,
        int paddingPx)
    {
        var xs = points.Select(p => LongitudeToX(p.Longitude, zoom)).ToList();
        var ys = points.Select(p => LatitudeToY(p.Latitude, zoom)).ToList();

        var centerX = (xs.Min() + xs.Max()) / 2.0;
        var centerY = (ys.Min() + ys.Max()) / 2.0;

        // Базовый масштаб 1.0; если трек не влезает с padding — уменьшаем
        var requiredX = (xs.Max() - xs.Min()) * TileSizePx;
        var requiredY = (ys.Max() - ys.Min()) * TileSizePx;
        var available = imageSize - paddingPx * 2;
        var scale = Math.Min(
            requiredX > 0 ? available / requiredX : double.MaxValue,
            requiredY > 0 ? available / requiredY : double.MaxValue);
        if (scale > 1.0)
        {
            scale = 1.0;
        }

        // Дробное начало сетки: центр bbox ровно в центре imageSize.
        // Раньше здесь был Floor — дробный остаток сдвигал контент к краю,
        // и при frac→1 маркер оказывался в правом нижнем углу.
        var originX = centerX - imageSize / (2.0 * TileSizePx * scale);
        var originY = centerY - imageSize / (2.0 * TileSizePx * scale);

        // Диапазон тайлов должен покрыть канву ЦЕЛИКОМ: [origin, origin + tilesAcross].
        // При дробном origin это на 1 тайл больше, чем floor(origin) + целые тайлы:
        // иначе правый/нижний край канвы остаётся без тайлов (серый фон).
        var tilesAcross = imageSize / (TileSizePx * scale);
        var minX = (int)Math.Floor(originX);
        var maxX = (int)Math.Ceiling(originX + tilesAcross) - 1;
        var minY = (int)Math.Floor(originY);
        var maxY = (int)Math.Ceiling(originY + tilesAcross) - 1;

        return new TileGrid(
            zoom, minX, maxX, minY, maxY,
            scale,
            originX: originX,
            originY: originY,
            canvasWidth: imageSize,
            canvasHeight: imageSize);
    }

    /// <summary>
    /// Подбирает зум, при котором весь трек помещается на изображение.
    /// </summary>
    public static int ChooseZoom(IReadOnlyList<GeoPoint> points, int imageSize, int paddingPx)
    {
        const int maxZoom = 17;

        for (var zoom = maxZoom; zoom >= 3; zoom--)
        {
            var xs = points.Select(p => LongitudeToX(p.Longitude, zoom)).ToList();
            var ys = points.Select(p => LatitudeToY(p.Latitude, zoom)).ToList();

            var widthPx = (xs.Max() - xs.Min()) * TileSizePx;
            var heightPx = (ys.Max() - ys.Min()) * TileSizePx;

            if (widthPx <= imageSize - paddingPx * 2 && heightPx <= imageSize - paddingPx * 2)
            {
                return zoom;
            }
        }

        return 3;
    }
}
