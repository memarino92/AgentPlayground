using AgentPlayground.Contracts.Messaging;
using FluentAssertions;
using Xunit;

namespace AgentPlayground.Contracts.Tests.Messaging;

public class PostgresConnectionStringNormalizerTests
{
    [Fact]
    public void Normalize_ReturnsNull_ForEmptyInput()
    {
        var normalized = PostgresConnectionStringNormalizer.Normalize(string.Empty);

        normalized.Should().BeNull();
    }

    [Fact]
    public void Normalize_ReturnsOriginal_ForNpgsqlStyleConnectionString()
    {
        const string input = "Host=localhost;Port=5432;Database=agentplayground;Username=user;Password=pass";

        var normalized = PostgresConnectionStringNormalizer.Normalize(input);

        normalized.Should().Be(input);
    }

    [Fact]
    public void Normalize_ConvertsPostgresUriToNpgsqlFormat()
    {
        const string input = "postgres://agent:secret@localhost:5433/agentplayground";

        var normalized = PostgresConnectionStringNormalizer.Normalize(input);

        normalized.Should().Be("Host=localhost;Port=5433;Database=agentplayground;Username=agent;Password=secret");
    }

    [Fact]
    public void Normalize_MapsKnownQueryKeys()
    {
        const string input = "postgresql://agent:secret@localhost/agentplayground?sslmode=require&trust_server_certificate=true&pooling=false";

        var normalized = PostgresConnectionStringNormalizer.Normalize(input);

        normalized.Should().Be("Host=localhost;Port=5432;Database=agentplayground;Username=agent;Password=secret;SSL Mode=require;Trust Server Certificate=true;Pooling=false");
    }

    [Fact]
    public void Normalize_PreservesUnknownQueryKeys()
    {
        const string input = "postgresql://agent:secret@localhost/agentplayground?application_name=agent-api";

        var normalized = PostgresConnectionStringNormalizer.Normalize(input);

        normalized.Should().Be("Host=localhost;Port=5432;Database=agentplayground;Username=agent;Password=secret;application_name=agent-api");
    }
}
