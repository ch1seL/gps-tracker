using GpsTracker.Services;
using Xunit;

namespace GpsTracker.Tests;

/// <summary>
/// Тесты разбора множителя /pos: пусто = 1, точка и запятая как разделитель,
/// диапазон [1, 10], отказ на мусоре.
/// </summary>
public class PositionZoomFactorTests
{
    [Fact]
    public void NoArgument_MeansDefaultZoom()
    {
        Assert.True(TelegramBotService.TryParseZoomFactor("/pos", out var factor));
        Assert.Equal(1.0, factor);
    }

    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("2,5", 2.5)] // запятая — тоже десятичный разделитель
    [InlineData("1", 1.0)]
    [InlineData("10", 10.0)]
    [InlineData("3", 3.0)]
    public void ValidArguments_AreParsed(string arg, double expected)
    {
        Assert.True(TelegramBotService.TryParseZoomFactor($"/pos {arg}", out var factor));
        Assert.Equal(expected, factor, precision: 6);
    }

    [Theory]
    [InlineData("/pos abc")]
    [InlineData("/pos 0")]
    [InlineData("/pos -1")]
    [InlineData("/pos 0.5")] // только уменьшение масштаба
    [InlineData("/pos 25")] // больше лимита
    [InlineData("/pos 2.5 3")] // два аргумента
    public void InvalidArguments_AreRejected(string command)
    {
        Assert.False(TelegramBotService.TryParseZoomFactor(command, out _));
    }
}
