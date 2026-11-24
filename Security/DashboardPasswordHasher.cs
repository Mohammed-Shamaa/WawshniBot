using System.Security.Cryptography;

namespace Wawshni.Bot.Security;

public static class DashboardPasswordHasher
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string CreateHash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? encoded)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024 || string.IsNullOrWhiteSpace(encoded)) return false;
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256"
                || !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 2_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length is < 16 or > 64 || expected.Length is < 24 or > 64) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
