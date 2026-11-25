using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wawshni.Bot.Services;

namespace Wawshni.Bot.Services;

/// <summary>Persists the ASP.NET Data Protection key ring in Neon across Render restarts.</summary>
public sealed class NeonDataProtectionXmlRepository(NeonDatabaseReadiness database) : IXmlRepository
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var dataSource = database.DataSource ?? throw new InvalidOperationException("Neon database is required for persistent dashboard authentication.");
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS aspnet_data_protection_keys (
                friendly_name TEXT PRIMARY KEY,
                key_xml TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var dataSource = database.DataSource ?? throw new InvalidOperationException("Neon database is required for persistent dashboard authentication.");
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT key_xml FROM aspnet_data_protection_keys ORDER BY created_at", connection);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read()) elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var dataSource = database.DataSource ?? throw new InvalidOperationException("Neon database is required for persistent dashboard authentication.");
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            INSERT INTO aspnet_data_protection_keys (friendly_name, key_xml)
            VALUES (@name, @xml)
            ON CONFLICT (friendly_name) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}

/// <summary>Encrypts persisted key-ring XML with a stable service secret using AES-256-GCM.</summary>
public sealed class NeonDataProtectionXmlEncryptor(IConfiguration configuration) : IXmlEncryptor
{
    private const string AssociatedData = "WawshniBot.DataProtection.KeyRing.v1";
    private readonly byte[] _key = ReadKey(configuration);

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(AssociatedData));

        var encryptedElement = new XElement("encryptedData",
            new XAttribute("version", "1"),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("ciphertext", Convert.ToBase64String(ciphertext)));
        CryptographicOperations.ZeroMemory(plaintext);
        return new EncryptedXmlInfo(encryptedElement, typeof(NeonDataProtectionXmlDecryptor));
    }

    internal static byte[] ReadKey(IConfiguration configuration)
    {
        var encoded = configuration["DataProtection:KeyEncryptionKey"];
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("DataProtection:KeyEncryptionKey must be configured to protect persistent dashboard keys.");

        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("DataProtection:KeyEncryptionKey must be a base64-encoded 32-byte key.", exception);
        }
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("DataProtection:KeyEncryptionKey must be a base64-encoded 32-byte key.");
        }
        return key;
    }
}

public sealed class NeonDataProtectionXmlDecryptor : IXmlDecryptor
{
    private const string AssociatedData = "WawshniBot.DataProtection.KeyRing.v1";
    private readonly byte[] _key;

    public NeonDataProtectionXmlDecryptor(IServiceProvider? services)
    {
        var configuration = services?.GetService<IConfiguration>()
            ?? throw new InvalidOperationException("Application configuration is unavailable for Data Protection key decryption.");
        _key = NeonDataProtectionXmlEncryptor.ReadKey(configuration);
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        if (encryptedElement.Name != "encryptedData" || (string?)encryptedElement.Attribute("version") != "1")
            throw new CryptographicException("Unsupported persistent Data Protection key format.");

        var nonce = Convert.FromBase64String(encryptedElement.Element("nonce")?.Value
            ?? throw new CryptographicException("Encrypted key nonce is missing."));
        var tag = Convert.FromBase64String(encryptedElement.Element("tag")?.Value
            ?? throw new CryptographicException("Encrypted key tag is missing."));
        var ciphertext = Convert.FromBase64String(encryptedElement.Element("ciphertext")?.Value
            ?? throw new CryptographicException("Encrypted key data is missing."));
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_key, tag.Length))
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(AssociatedData));

        try { return XElement.Parse(Encoding.UTF8.GetString(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
