using Microsoft.Extensions.Options;
using Wawshni.Bot.Users;

namespace Wawshni.Bot.Security;

public sealed class TelegramUserAuthorization(
    IOptions<TelegramOptions> options,
    TelegramUserRepository users,
    ILogger<TelegramUserAuthorization> logger)
{
    public long? AdministratorId => options.Value.AuthorizedUserId;
    public bool IsAdministrator(long? userId) => userId.HasValue && userId == AdministratorId;

    public async Task<TelegramRegistrationStatus?> GetStatusAsync(long userId, CancellationToken cancellationToken) =>
        IsAdministrator(userId) ? TelegramRegistrationStatus.Approved
            : (await users.GetByTelegramIdAsync(userId, cancellationToken))?.Status;

    public async Task<bool> IsApprovedAsync(long userId, CancellationToken cancellationToken) =>
        TelegramRegistrationPolicy.CanAccessProtectedFeatures(await GetStatusAsync(userId, cancellationToken), IsAdministrator(userId));

    public async Task<bool> IsAdminAsync(long userId, CancellationToken cancellationToken)
    {
        if (AdministratorId is null)
        {
            logger.LogWarning("Telegram:AuthorizedUserId is not configured; administrator operations are disabled.");
            return false;
        }
        return IsAdministrator(userId);
    }
}
