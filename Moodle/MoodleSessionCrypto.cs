using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Wawshni.Bot.Moodle;

public sealed record EncryptedMoodleSession(byte[] Ciphertext, byte[] Nonce, byte[] Tag, short KeyVersion);

public sealed class MoodleSessionCrypto(IOptions<MoodleOptions> options)
{
    public EncryptedMoodleSession Encrypt(long telegramUserId, string storageState)
    {
        var version = options.Value.SessionEncryptionKeyVersion;
        var key = GetKey(version);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(storageState);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(telegramUserId, version));
            return new EncryptedMoodleSession(ciphertext, nonce, tag, version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Decrypt(long telegramUserId, EncryptedMoodleSession session)
    {
        var key = GetKey(session.KeyVersion);
        var plaintext = new byte[session.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, session.Tag.Length);
            aes.Decrypt(session.Nonce, session.Ciphertext, session.Tag, plaintext, AssociatedData(telegramUserId, session.KeyVersion));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] GetKey(short version)
    {
        var settings = options.Value;
        var configured = version == settings.SessionEncryptionKeyVersion
            ? settings.SessionEncryptionKey
            : settings.PreviousSessionEncryptionKeys.GetValueOrDefault(version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Moodle session encryption is not configured.");
        try
        {
            var key = Convert.FromBase64String(configured);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException) { }
        throw new InvalidOperationException("Moodle session encryption key must be Base64 encoding of 32 random bytes.");
    }

    private static byte[] AssociatedData(long userId, short version) => Encoding.UTF8.GetBytes($"wawshni:moodle-session:v{version}:telegram:{userId}");
}
