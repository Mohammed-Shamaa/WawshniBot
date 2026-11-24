using Wawshni.Bot.Users;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class TelegramRegistrationPolicyTests
{
    [Fact]
    public void AdministratorAlwaysHasProtectedAccess()
    {
        Assert.True(TelegramRegistrationPolicy.CanAccessProtectedFeatures(null, isAdministrator: true));
        Assert.True(TelegramRegistrationPolicy.CanAccessProtectedFeatures(TelegramRegistrationStatus.Rejected, isAdministrator: true));
    }

    [Theory]
    [InlineData(TelegramRegistrationStatus.Approved, true)]
    [InlineData(TelegramRegistrationStatus.Pending, false)]
    [InlineData(TelegramRegistrationStatus.Rejected, false)]
    public void BlockingDeniesAccessWithoutChangingRegistrationStatus(TelegramRegistrationStatus originalStatus, bool wasAllowed)
    {
        Assert.Equal(wasAllowed, TelegramRegistrationPolicy.CanAccessProtectedFeatures(originalStatus, isAdministrator: false));
        Assert.False(TelegramRegistrationPolicy.CanAccessProtectedFeatures(originalStatus, isAdministrator: false, isBlocked: true));
    }

    [Fact]
    public void BlockingOverridesAdministratorTelegramAccess()
    {
        Assert.False(TelegramRegistrationPolicy.CanAccessProtectedFeatures(TelegramRegistrationStatus.Approved, isAdministrator: true, isBlocked: true));
    }

    [Theory]
    [InlineData(TelegramRegistrationStatus.Approved, true)]
    [InlineData(TelegramRegistrationStatus.Pending, false)]
    [InlineData(TelegramRegistrationStatus.Rejected, false)]
    public void ProtectedAccessDependsOnPersistedStatus(TelegramRegistrationStatus status, bool expected)
    {
        Assert.Equal(expected, TelegramRegistrationPolicy.CanAccessProtectedFeatures(status, isAdministrator: false));
    }

    [Theory]
    [InlineData(TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Approved, true)]
    [InlineData(TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Rejected, true)]
    [InlineData(TelegramRegistrationStatus.Rejected, TelegramRegistrationStatus.Approved, true)]
    [InlineData(TelegramRegistrationStatus.Approved, TelegramRegistrationStatus.Rejected, true)]
    [InlineData(TelegramRegistrationStatus.Approved, TelegramRegistrationStatus.Pending, false)]
    [InlineData(TelegramRegistrationStatus.Rejected, TelegramRegistrationStatus.Pending, false)]
    [InlineData(TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Pending, false)]
    public void OnlyExplicitAdministrativeTransitionsAreAllowed(TelegramRegistrationStatus current, TelegramRegistrationStatus target, bool expected)
    {
        Assert.Equal(expected, TelegramRegistrationPolicy.CanTransition(current, target));
    }
}
