using AgentPlayground.Integrations;
using Bunit;
using FluentAssertions;
using PersonalAgent.Web.Components.Integrations;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class IntegrationSettingsTests : TestContext
{
    [Fact]
    public void KeepSecret_DoesNotSendOrRevealIt()
    {
        SaveIntegrationRequest? saved = null;
        var cut = RenderComponent<SentrySettingsForm>(Parameters => Parameters
            .Add(Component => Component.Settings, Settings())
            .Add(Component => Component.OnSave, Request => saved = Request));
        cut.Find("#sentry-environment").Change("staging");
        cut.Find("form").Submit();
        saved.Should().NotBeNull();
        saved!.ExpectedRevision.Should().Be(1);
        saved.Values.Should().NotContainKey("Dsn");
        saved.Values["Environment"].Should().Be("staging");
        cut.FindAll("#sentry-dsn").Should().BeEmpty();
    }

    [Fact]
    public void InvalidReplacement_BlocksSave_AndClearRequiresDisabledReporting()
    {
        SaveIntegrationRequest? saved = null;
        var cut = RenderComponent<SentrySettingsForm>(Parameters => Parameters
            .Add(Component => Component.Settings, Settings())
            .Add(Component => Component.OnSave, Request => saved = Request));
        cut.Find("#sentry-secret-action").Change("replace");
        cut.Find("#sentry-dsn").Change("http://localhost/private");
        cut.Find("form").Submit();
        saved.Should().BeNull();
        cut.Markup.Should().Contain("Enter a hosted Sentry HTTPS DSN");
        cut.Find("#sentry-secret-action").Change("clear");
        cut.Find("form").Submit();
        saved.Should().BeNull();
        cut.Find("input[type=checkbox]").Change(false);
        cut.Find("form").Submit();
        saved!.Values["Dsn"].Should().BeEmpty();
        saved.Values["Enabled"].Should().Be("false");
    }

    [Fact]
    public void NewSavedRevision_ClearsEnteredSecret()
    {
        var cut = RenderComponent<SentrySettingsForm>(Parameters => Parameters.Add(Component => Component.Settings, Settings()));
        cut.Find("#sentry-secret-action").Change("replace");
        cut.Find("#sentry-dsn").Change("private-value");
        cut.SetParametersAndRender(Parameters => Parameters.Add(Component => Component.Settings, Settings() with { SavedRevision = 2 }));
        cut.FindAll("#sentry-dsn").Should().BeEmpty();
        cut.Markup.Should().NotContain("private-value");
    }

    [Fact]
    public void Status_DistinguishesOfflineMissingAndPendingServices()
    {
        var cut = RenderComponent<IntegrationServiceStatus>(Parameters => Parameters.Add(Component => Component.Settings, Settings() with
        {
            ActiveRevision = 2,
            Instances = [new("Api", "instance-a", 1, "Applied", ["SENTRY_DSN"], DateTimeOffset.UtcNow),
                new("Web", "instance-w", 2, "Applied", [], DateTimeOffset.UtcNow.AddMinutes(-2))]
        }));
        cut.Markup.Should().Contain("Update pending").And.Contain("Offline or not reporting").And.Contain("Not yet seen").And.Contain("SENTRY_DSN");
    }

    [Fact]
    public void Status_ShowsFriendlyServices_AndMovesOldInstancesIntoCollapsedDetails()
    {
        var cut = RenderComponent<IntegrationServiceStatus>(Parameters => Parameters.Add(Component => Component.Settings, Settings() with
        {
            Instances = [new("Api", "old-instance", 0, "Applied", [], DateTimeOffset.UtcNow.AddMinutes(-2)),
                new("Api", "current-instance", 1, "Applied", [], DateTimeOffset.UtcNow)]
        }));
        var overview = cut.Find("table");
        overview.QuerySelectorAll("tbody tr").Length.Should().Be(3);
        overview.TextContent.Should().Contain("API service").And.Contain("Web application").And.Contain("Background worker").And.Contain("Up to date");
        overview.TextContent.Should().NotContain("old-instance").And.NotContain("current-instance").And.NotContain("Offline");
        cut.Find("details").HasAttribute("open").Should().BeFalse();
        cut.Find("details").TextContent.Should().Contain("old-instance");
    }

    [Fact]
    public void Status_DoesNotHideAPendingLiveReplicaBehindAHealthyOne()
    {
        var cut = RenderComponent<IntegrationServiceStatus>(Parameters => Parameters.Add(Component => Component.Settings, Settings() with
        {
            Instances = [new("Api", "ready", 1, "Applied", [], DateTimeOffset.UtcNow),
                new("Api", "pending", 0, "Applied", [], DateTimeOffset.UtcNow)]
        }));
        cut.Find("table").TextContent.Should().Contain("1 of 2 instances need attention");
    }

    private static IntegrationSettingsResponse Settings() => new("sentry", 1, 1, IntegrationRegistry.Fields,
        new() { ["Enabled"] = "true", ["Environment"] = "production", ["SampleRate"] = "1" }, ["Dsn"], []);
}
