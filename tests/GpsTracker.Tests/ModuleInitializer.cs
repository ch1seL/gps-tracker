using System.Globalization;
using System.Runtime.CompilerServices;

namespace GpsTracker.Tests;

/// <summary>
/// Тестовый хост должен работать в инвариантной культуре: продакшн-код парсит
/// протокол с InvariantCulture, а машина разработчика может быть в ru-RU
/// (запятая в качестве десятичного разделителя искажала бы ожидания).
/// </summary>
internal static class ModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
