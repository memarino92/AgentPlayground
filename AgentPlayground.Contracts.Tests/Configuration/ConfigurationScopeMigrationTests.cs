using System.Security.Cryptography;
using AgentPlayground.Contracts.Configuration;
using ConfigurationScopeMigration;
using FluentAssertions;
using Xunit;

namespace AgentPlayground.Contracts.Tests.Configuration;

public class ConfigurationScopeMigrationTests
{
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private const string Key = "AssemblyAi:ApiKey";

    [Fact]
    public void Move_ReencryptsWithNewScope_AndPreservesSecret()
    {
        var Source = Secret("Worker", "synthetic-key");
        var Move = MigrationPlan.Create([Source], MasterKey).Single();
        PostgresConfigurationCrypto.Decrypt(Move.EncryptedValue, "Api", Key, MasterKey).Should().Be("synthetic-key");
        Move.EncryptedValue.Should().NotBe(Source.Value);
        var WrongScope = () => PostgresConfigurationCrypto.Decrypt(Move.EncryptedValue, "Worker", Key, MasterKey);
        WrongScope.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void MatchingDestination_IsPreserved_AndRerunIsEmpty()
    {
        var Destination = Secret("Api", "synthetic-key");
        MigrationPlan.Create([Secret("Worker", "synthetic-key"), Destination], MasterKey).Single().DestinationExists.Should().BeTrue();
        MigrationPlan.Create([Destination], MasterKey).Should().BeEmpty();
    }

    [Fact]
    public void ConflictingDestination_AbortsPlan()
    {
        var Act = () => MigrationPlan.Create([Secret("Worker", "old"), Secret("Api", "new")], MasterKey);
        Act.Should().Throw<InvalidOperationException>().WithMessage("An Api setting differs*");
    }

    [Fact]
    public void WrongMasterKey_RejectsMigration()
    {
        var Act = () => MigrationPlan.Create([Secret("Worker", "synthetic-key")], Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void PlainSettings_PreserveValueAndActivation_AndIgnoreOtherProviders()
    {
        var Moves = MigrationPlan.Create([
            new("Worker", "AssemblyAi:PollIntervalSeconds", "8", false, false),
            new("Worker", "GitHub:RepoOwner", "other", false, true)], MasterKey);
        Moves.Should().ContainSingle();
        Moves[0].EncryptedValue.Should().Be("8");
        Moves[0].Source.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ConflictingActivation_AbortsPlan()
    {
        var Source = Secret("Worker", "synthetic-key");
        var Destination = Secret("Api", "synthetic-key") with { IsActive = false };
        var Act = () => MigrationPlan.Create([Source, Destination], MasterKey);
        Act.Should().Throw<InvalidOperationException>();
    }

    private static Setting Secret(string Scope, string Value) => new(Scope, Key,
        PostgresConfigurationCrypto.Encrypt(Value, Scope, Key, MasterKey), true, true);
}
