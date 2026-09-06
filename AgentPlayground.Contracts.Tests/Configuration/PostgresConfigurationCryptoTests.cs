using System.Security.Cryptography;
using AgentPlayground.Contracts.Configuration;
using FluentAssertions;
using Xunit;

namespace AgentPlayground.Contracts.Tests.Configuration;

public class PostgresConfigurationCryptoTests
{
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void EncryptAndDecrypt_RoundTripsValue()
    {
        var encrypted = PostgresConfigurationCrypto.Encrypt("secret-value", "Api", "OpenAI:ApiKey", MasterKey);

        var decrypted = PostgresConfigurationCrypto.Decrypt(encrypted, "Api", "OpenAI:ApiKey", MasterKey);

        decrypted.Should().Be("secret-value");
        encrypted.Should().NotContain("secret-value");
    }

    [Fact]
    public void Decrypt_RejectsValueMovedToAnotherKey()
    {
        var encrypted = PostgresConfigurationCrypto.Encrypt("secret-value", "Api", "OpenAI:ApiKey", MasterKey);

        var action = () => PostgresConfigurationCrypto.Decrypt(encrypted, "Api", "Tavily:ApiKey", MasterKey);

        action.Should().Throw<CryptographicException>();
    }
}
