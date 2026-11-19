namespace Wawshni.Bot;

public sealed class TelegramOptions
{
    public const string SectionName = "TelegramBot";
    public string Token { get; set; } = string.Empty;
    public long? AuthorizedUserId { get; set; }
}
