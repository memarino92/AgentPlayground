using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

using PersonalAgent.Api.Coding;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingSubscriptionTests
{
    private const string Model = "chatgpt:cb8c5a00-985c-4504-9781-deb1a836a500:gpt-6-astra";

    [Fact]
    public void SubscriptionAllowsLargeRequestLimitsWithoutDollarReservations()
    {
        CodingJobStore.ValidateSettings(new() { ModelId = Model, MaxModelRequests = 1000, ModelBudgetUsd = 0 });
        FluentActions.Invoking(() => CodingJobStore.ValidateSettings(new() { ModelId = Model, MaxModelRequests = 1001 })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CodingJobStore.ValidateSettings(new() { MaxModelRequests = 250, ModelBudgetUsd = 100 })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CodingJobStore.ValidateSettings(new() { ModelId = "chatgpt:not-a-connection:gpt-6-astra" })).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResponsesReplayContextAndLocalToolsWithSubscriptionRequirements()
    {
        var Input = JsonNode.Parse("""
            {"model":"gpt-6-astra","store":true,"stream":false,"previous_response_id":"remote",
            "max_output_tokens":50,"temperature":1,"metadata":{"private":"value"},"service_tier":"priority",
            "input":[{"type":"message","role":"system","content":[{"type":"input_text","text":"Rules"}]},
                {"type":"function_call","call_id":"call_1","name":"bash","arguments":"{}"},
                {"type":"function_call_output","call_id":"call_1","output":"passed"},
                {"type":"reasoning","id":"rs_1","encrypted_content":"encrypted"}],
            "tools":[{"type":"function","name":"bash","description":"Run locally","parameters":{"type":"object","properties":{}}}],
            "tool_choice":{"type":"function","name":"bash"}}
            """)!.AsObject();
        var Safe = CodingSubscriptionRequest.Sanitize(Input, "gpt-6-astra");
        Safe["store"]!.GetValue<bool>().Should().BeFalse();
        Safe["stream"]!.GetValue<bool>().Should().BeTrue();
        foreach (var Key in new[] { "previous_response_id", "max_output_tokens", "temperature", "metadata", "service_tier" })
            Safe.ContainsKey(Key).Should().BeFalse();
        Safe["input"]![0]!["role"]!.GetValue<string>().Should().Be("developer");
        Safe["input"]![1]!["namespace"]!.GetValue<string>().Should().Be("coding");
        Safe["input"]![2]!["output"]!.GetValue<string>().Should().Be("passed");
        Safe["input"]![3]!["encrypted_content"]!.GetValue<string>().Should().Be("encrypted");
        Safe["tools"]![0]!["type"]!.GetValue<string>().Should().Be("namespace");
        Safe["tools"]![0]!["tools"]![0]!["name"]!.GetValue<string>().Should().Be("bash");
        Safe["tool_choice"]!["namespace"]!.GetValue<string>().Should().Be("coding");
        Input["store"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"model\":\"different\",\"input\":[{\"role\":\"user\",\"content\":\"hi\"}]}")]
    [InlineData("{\"model\":\"gpt-6-astra\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"remote\"}]}]}")]
    [InlineData("{\"model\":\"gpt-6-astra\",\"input\":[{\"type\":\"item_reference\",\"id\":\"remote\"}]}")]
    [InlineData("{\"model\":\"gpt-6-astra\",\"input\":[{\"role\":\"user\",\"content\":\"hi\"}],\"tools\":[{\"type\":\"web_search\"}]}")]
    public void UnsupportedModelRemoteContextAndHostedToolsAreRejected(string Input) =>
        FluentActions.Invoking(() => CodingSubscriptionRequest.Sanitize(JsonNode.Parse(Input)!.AsObject(), "gpt-6-astra")).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("response.completed", true)]
    [InlineData("response.failed", false)]
    [InlineData("response.incomplete", false)]
    [InlineData("response.output_text.delta", false)]
    public async Task SubscriptionStreamMustCompleteAndMasksProviderFailures(string Type, bool Success)
    {
        var Stream = "event: " + Type + "\ndata: {\"type\":\"" + Type + "\",\"private\":\"private-provider-body\"}\n\n";
        using var Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
        var Context = new DefaultHttpContext();
        Context.Response.Body = new MemoryStream();
        var Relay = () => CodingModelGateway.RelaySubscriptionStreamAsync(Response, Context, default);
        if (Success) await Relay();
        else
        {
            var Failure = await FluentActions.Awaiting(Relay).Should().ThrowAsync<CodingGatewayException>();
            Failure.Which.Message.Should().NotContain("private-provider-body");
        }
    }
}
