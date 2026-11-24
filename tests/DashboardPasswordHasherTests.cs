using Wawshni.Bot.Security;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class DashboardPasswordHasherTests
{
    [Fact]
    public void GeneratedHashVerifiesOnlyTheOriginalPassword()
    {
        var encoded = DashboardPasswordHasher.CreateHash("correct horse battery staple");

        Assert.StartsWith("pbkdf2-sha256$600000$", encoded);
        Assert.True(DashboardPasswordHasher.Verify("correct horse battery staple", encoded));
        Assert.False(DashboardPasswordHasher.Verify("incorrect password", encoded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$1$bad$bad")]
    public void MalformedHashIsRejected(string? encoded)
    {
        Assert.False(DashboardPasswordHasher.Verify("any password", encoded));
    }
}
