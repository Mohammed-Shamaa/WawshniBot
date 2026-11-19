using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Wawshni.Bot.Moodle;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class MoodleSessionCryptoTests
{
    [Fact]
    public void Encrypted_state_round_trips_and_is_bound_to_telegram_account()
    {
        var crypto = CreateCrypto();
        const long ownerId = 123456789;
        const string state = "{\"cookies\":[{\"name\":\"session\",\"value\":\"secret-session-value\"}],\"origins\":[]}";

        var encrypted = crypto.Encrypt(ownerId, state);

        Assert.NotEqual(state, Convert.ToBase64String(encrypted.Ciphertext));
        Assert.Equal(state, crypto.Decrypt(ownerId, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(ownerId + 1, encrypted));
    }

    [Fact]
    public void Tampered_state_is_rejected()
    {
        var crypto = CreateCrypto();
        var encrypted = crypto.Encrypt(42, "{\"cookies\":[],\"origins\":[]}");
        var changedCiphertext = encrypted.Ciphertext.ToArray();
        changedCiphertext[0] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(42, encrypted with { Ciphertext = changedCiphertext }));
    }

    private static MoodleSessionCrypto CreateCrypto()
    {
        var options = Options.Create(new MoodleOptions
        {
            SessionEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            SessionEncryptionKeyVersion = 7
        });
        return new MoodleSessionCrypto(options);
    }
}
