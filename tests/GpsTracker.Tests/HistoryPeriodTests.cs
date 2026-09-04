using GpsTracker.Services;
using Xunit;

namespace GpsTracker.Tests;

/// <summary>
/// Тесты разбора периода /history: относительные форматы (2d, 6h, 30m),
/// диапазон дат (AssumeUniversal — даты читаются как UTC) и отказ на мусоре.
/// </summary>
public class HistoryPeriodTests
{
    [Fact]
    public void EmptyCommand_MeansLast24Hours()
    {
        var before = DateTimeOffset.UtcNow;

        var period = TelegramBotService.ParseHistoryPeriod("/history");

        Assert.NotNull(period);
        var (from, to) = period.Value;
        Assert.InRange(to, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(TimeSpan.FromHours(24), to - from);
    }

    [Theory]
    [InlineData("2d", 2 * 24 * 60)]
    [InlineData("6h", 6 * 60)]
    [InlineData("30m", 30)]
    [InlineData("90D", 90 * 24 * 60)] // регистр не важен
    public void RelativeFormats_MatchExpectedSpan(string arg, int expectedMinutes)
    {
        var period = TelegramBotService.ParseHistoryPeriod($"/history {arg}");

        Assert.NotNull(period);
        var (from, to) = period.Value;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), to - from);
        Assert.InRange(to, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void DateRange_ReadsDatesAsUtcMidnights()
    {
        var period = TelegramBotService.ParseHistoryPeriod("/history 2026-01-15 2026-01-16");

        Assert.NotNull(period);
        var (from, to) = period.Value;
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero), to);
    }

    [Theory]
    [InlineData("/history abc")]
    [InlineData("/history 2026-01-15")] // одна дата без пары — не диапазон
    [InlineData("/history 15.01.2026 16.01.2026")] // только ISO-даты
    public void InvalidInput_ReturnsNull(string command)
    {
        Assert.Null(TelegramBotService.ParseHistoryPeriod(command));
    }
}
