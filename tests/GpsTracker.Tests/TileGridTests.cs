using GpsTracker.Services;
using Xunit;

namespace GpsTracker.Tests;

/// <summary>
/// Тесты раскладки тайлов: центр bounding box точек должен попадать точно
/// в центр канвы независимо от выравнивания тайловой сетки (раньше Floor
/// origin сдвигал контент к краю — маркер «гулял» до правого нижнего угла).
/// </summary>
public class TileGridTests
{
    private static readonly GeoPoint[] SinglePoint =
        { new(60.1158, 31.3798) };

    [Fact]
    public void SinglePoint_LandsAtCanvasCenter()
    {
        var grid = TileGrid.Calculate(SinglePoint, zoom: 15, imageSize: 512, paddingPx: 96);

        var px = grid.Project(60.1158, 31.3798);

        Assert.Equal(256, px.X, precision: 0);
        Assert.Equal(256, px.Y, precision: 0);
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void BboxCenter_AtCanvasCenter_ForAnyTileAlignment(double frac)
    {
        // Сдвигаем точку на frac тайла: раньше дробный остаток origin сдвигал маркер
        var lon = 31.3798 + frac * (360.0 / (1 << 15)) / 4;
        var grid = TileGrid.Calculate(new[] { new GeoPoint(60.1158, lon) }, zoom: 15, imageSize: 512, paddingPx: 96);

        var px = grid.Project(60.1158, lon);

        // Центр bbox (одна точка = сама точка) всегда в центре канвы ±пиксель
        Assert.Equal(256, px.X, precision: 0);
        Assert.Equal(256, px.Y, precision: 0);
    }

    [Fact]
    public void TrackPoints_FitIntoCanvasWithPadding()
    {
        var points = new[]
        {
            new GeoPoint(60.1158, 31.3798),
            new GeoPoint(60.1258, 31.3898),
            new GeoPoint(60.1058, 31.3698)
        };

        var grid = TileGrid.Calculate(points, zoom: 15, imageSize: 512, paddingPx: 48);

        foreach (var p in points)
        {
            var px = grid.Project(p.Latitude, p.Longitude);
            Assert.InRange(px.X, 0, grid.Width);
            Assert.InRange(px.Y, 0, grid.Height);
        }

        // Центр bbox — в центре канвы
        var cxs = points.Select(p => grid.Project(p.Latitude, p.Longitude).X).ToList();
        var cys = points.Select(p => grid.Project(p.Latitude, p.Longitude).Y).ToList();
        Assert.Equal(grid.Width / 2.0, (cxs.Min() + cxs.Max()) / 2.0, precision: 0);
        Assert.Equal(grid.Height / 2.0, (cys.Min() + cys.Max()) / 2.0, precision: 0);
    }

    [Fact]
    public void ZoomedOutFit_KeepsCenterAndCanvasSize()
    {
        // Широкий трек: fitScale < 1 — канва остаётся imageSize, центр сохраняется
        var points = new[]
        {
            new GeoPoint(59.9, 30.2),
            new GeoPoint(60.3, 32.5)
        };

        var grid = TileGrid.Calculate(points, zoom: 15, imageSize: 512, paddingPx: 48);

        Assert.Equal(512, grid.Width);
        Assert.Equal(512, grid.Height);

        var p1 = grid.Project(59.9, 30.2);
        var p2 = grid.Project(60.3, 32.5);
        Assert.Equal(256, (p1.X + p2.X) / 2.0, precision: 0);
        Assert.Equal(256, (p1.Y + p2.Y) / 2.0, precision: 0);

        // Обе точки на канве
        Assert.InRange(p1.X, 0, 512);
        Assert.InRange(p2.X, 0, 512);
        Assert.InRange(p1.Y, 0, 512);
        Assert.InRange(p2.Y, 0, 512);
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void TileRange_CoversWholeCanvas_AtAnyAlignment(double frac)
    {
        // Регрессия: при дробном Origin правый/нижний край канвы оставался без тайлов
        var lon = 31.3798 + frac * (360.0 / (1 << 15)) / 4;
        var grid = TileGrid.Calculate(new[] { new GeoPoint(60.1158, lon) }, zoom: 15, imageSize: 512, paddingPx: 96);

        // Крайние тайлы должны перекрывать канву по обеим осям (с запасом ≥ 0)
        Assert.True((grid.MinX - grid.OriginX) * 256 * grid.Scale <= 0, "левый край не покрыт");
        Assert.True((grid.MaxX + 1 - grid.OriginX) * 256 * grid.Scale >= 512, "правый край не покрыт");
        Assert.True((grid.MinY - grid.OriginY) * 256 * grid.Scale <= 0, "верхний край не покрыт");
        Assert.True((grid.MaxY + 1 - grid.OriginY) * 256 * grid.Scale >= 512, "нижний край не покрыт");
    }

    [Fact]
    public void TileRange_CoversWholeCanvas_WhenZoomedOut()
    {
        // fitScale < 1: на канве помещается больше мира — тайлов нужно больше
        var points = new[]
        {
            new GeoPoint(59.9, 30.2),
            new GeoPoint(60.3, 32.5)
        };

        var grid = TileGrid.Calculate(points, zoom: 15, imageSize: 512, paddingPx: 48);

        Assert.True((grid.MinX - grid.OriginX) * 256 * grid.Scale <= 0, "левый край не покрыт");
        Assert.True((grid.MaxX + 1 - grid.OriginX) * 256 * grid.Scale >= 512, "правый край не покрыт");
        Assert.True((grid.MinY - grid.OriginY) * 256 * grid.Scale <= 0, "верхний край не покрыт");
        Assert.True((grid.MaxY + 1 - grid.OriginY) * 256 * grid.Scale >= 512, "нижний край не покрыт");
    }
}
