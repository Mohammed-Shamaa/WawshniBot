namespace Wawshni.Bot.Users;

public enum TelegramRegistrationStatus { Pending, Approved, Rejected }

public sealed record TelegramUserRegistration(
    Guid RequestId,
    long TelegramUserId,
    string? FirstName,
    string? LastName,
    string? Username,
    DateTimeOffset RegisteredAt,
    TelegramRegistrationStatus Status,
    DateTimeOffset StatusUpdatedAt,
    DateTimeOffset? DecisionAt,
    long? DecisionBy)
{
    public bool IsBlocked { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
    public string? BlockReason { get; init; }
    public string? BlockedBy { get; init; }
    public DateTimeOffset? UnblockedAt { get; init; }
    public string? UnblockedBy { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
}

public sealed record TelegramRegistrationResult(TelegramUserRegistration User, bool Created);

public static class TelegramRegistrationStatusExtensions
{
    public static string ToDatabaseValue(this TelegramRegistrationStatus status) => status.ToString();

    public static TelegramRegistrationStatus ParseDatabaseValue(string value) =>
        Enum.TryParse<TelegramRegistrationStatus>(value, ignoreCase: false, out var status)
            ? status
            : throw new InvalidOperationException("Unknown Telegram registration status in database.");
}

public static class TelegramRegistrationPolicy
{
    public static bool CanAccessProtectedFeatures(TelegramRegistrationStatus? status, bool isAdministrator, bool isBlocked = false) =>
        !isBlocked && (isAdministrator || status == TelegramRegistrationStatus.Approved);

    public static bool CanTransition(TelegramRegistrationStatus current, TelegramRegistrationStatus target) =>
        (current, target) is
            (TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Approved) or
            (TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Rejected) or
            (TelegramRegistrationStatus.Approved, TelegramRegistrationStatus.Rejected) or
            (TelegramRegistrationStatus.Rejected, TelegramRegistrationStatus.Approved);
}
