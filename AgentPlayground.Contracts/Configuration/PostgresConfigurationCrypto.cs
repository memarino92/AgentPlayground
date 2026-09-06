using System.Security.Cryptography;
using System.Text;

namespace AgentPlayground.Contracts.Configuration;

public static class PostgresConfigurationCrypto
{
    private const string Prefix = "v1";

    public static string Encrypt(string plaintext, string scope, string key, string base64MasterKey)
    {
        var masterKey = GetMasterKey(base64MasterKey);
        var encryptionKey = DeriveKey(masterKey, "configuration-encryption");
        var authenticationKey = DeriveKey(masterKey, "configuration-authentication");
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        var tag = HMACSHA256.HashData(authenticationKey, BuildAuthenticatedData(scope, key, aes.IV, ciphertext));
        return $"{Prefix}:{Convert.ToBase64String(aes.IV)}:{Convert.ToBase64String(ciphertext)}:{Convert.ToBase64String(tag)}";
    }

    public static string Decrypt(string encryptedValue, string scope, string key, string base64MasterKey)
    {
        var masterKey = GetMasterKey(base64MasterKey);

        var segments = encryptedValue.Split(':');
        if (segments is not [Prefix, _, _, _]) throw new InvalidOperationException($"Configuration value '{scope}:{key}' has an unsupported encrypted format.");

        var iv = Convert.FromBase64String(segments[1]);
        var ciphertext = Convert.FromBase64String(segments[2]);
        var suppliedTag = Convert.FromBase64String(segments[3]);
        var encryptionKey = DeriveKey(masterKey, "configuration-encryption");
        var authenticationKey = DeriveKey(masterKey, "configuration-authentication");
        var authenticatedData = BuildAuthenticatedData(scope, key, iv, ciphertext);
        var expectedTag = HMACSHA256.HashData(authenticationKey, authenticatedData);
        if (!CryptographicOperations.FixedTimeEquals(suppliedTag, expectedTag))
            throw new CryptographicException($"Configuration value '{scope}:{key}' failed authentication.");

        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(byte[] masterKey, string purpose) =>
        HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes(purpose));

    private static byte[] GetMasterKey(string base64MasterKey)
    {
        byte[] masterKey;
        try
        {
            masterKey = Convert.FromBase64String(base64MasterKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("CONFIG_ENCRYPTION_KEY must be a base64-encoded 32-byte key.", ex);
        }
        if (masterKey.Length is not 32) throw new InvalidOperationException("CONFIG_ENCRYPTION_KEY must be a base64-encoded 32-byte key.");
        return masterKey;
    }

    private static byte[] BuildAuthenticatedData(string scope, string key, byte[] iv, byte[] ciphertext)
    {
        var prefix = Encoding.UTF8.GetBytes($"{scope}\n{key}\n");
        return [.. prefix, .. iv, .. ciphertext];
    }
}
