using Microsoft.Extensions.Options;
namespace Wawshni.Bot.Security;
public sealed class TelegramUserAuthorization(IOptions<TelegramOptions> options, ILogger<TelegramUserAuthorization> logger)
{
    private readonly long? _authorizedUserId = options.Value.AuthorizedUserId;
    public bool IsAuthorized(long? userId)
    {
        if (_authorizedUserId is null) { logger.LogWarning("Telegram:AuthorizedUserId is not configured; interactions are disabled."); return false; }
        if (userId != _authorizedUserId) { logger.LogWarning("Rejected Telegram interaction from an unauthorized account."); return false; }
        return true;
    }
}
