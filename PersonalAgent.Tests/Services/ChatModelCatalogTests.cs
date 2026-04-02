using FluentAssertions;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class ChatModelCatalogTests
{
    [Fact]
    public void GetDefaultModel_ReturnsConfiguredDefault()
    {
        var options = Options.Create(new ChatModelCatalogOptions
        {
            Models =
            [
                new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini" },
                new ChatModelOption { Id = "gpt-4.1-mini", DisplayName = "GPT-4.1 mini", IsDefault = true }
            ]
        });

        var catalog = new ChatModelCatalog(options);

        catalog.GetDefaultModel().Id.Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public void FindModel_ReturnsDefault_WhenModelIdIsMissing()
    {
        var options = Options.Create(new ChatModelCatalogOptions
        {
            Models =
            [
                new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", IsDefault = true },
                new ChatModelOption { Id = "gpt-4.1-mini", DisplayName = "GPT-4.1 mini" }
            ]
        });

        var catalog = new ChatModelCatalog(options);

        catalog.FindModel(null)?.Id.Should().Be("gpt-4o-mini");
    }
}
