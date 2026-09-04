namespace GpsTracker.Configuration;

public class TelegramSettings
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Пустой список = разрешены все чаты. Иначе — только указанные chat id.
    /// </summary>
    public long[] AllowedChatIds { get; set; } = Array.Empty<long>();
}
