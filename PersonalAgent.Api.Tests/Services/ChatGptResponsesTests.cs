using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Npgsql;
using OpenAI;
using OpenAI.Responses;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;
using PersonalAgent.Integrations.ChatGpt;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

#pragma warning disable OPENAI001 // Exercise the same confined experimental Responses adapter as production.
public sealed class ChatGptResponsesTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionRequests_AlwaysStreamWithoutStorage_AndRequireCompletion(bool Interrupted)
    {
        var store = await SeedAsync();
        using var handler = new ResponsesHandler(Interrupted);
        using var http = new HttpClient(handler);
        using var client = new ChatGptChatClient(store, "owner", ConnectionId, "gpt-6.1-sol", Credential =>
            new ResponsesClient(new ApiKeyCredential(Credential), new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
            }).AsIChatClient("gpt-6.1-sol"));
        var response = async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
        if (Interrupted) await response.Should().ThrowAsync<ChatGptException>();
        else (await response()).Text.Should().Be("Hello");
        handler.Called.Should().BeTrue();
    }

    [Fact]
    public async Task AnotherActor_CannotDispatchWithASavedConnection()
    {
        var store = await SeedAsync();
        var dispatched = false;
        using var client = new ChatGptChatClient(store, "another-owner", ConnectionId, "gpt-6.1-sol", _ =>
        {
            dispatched = true;
            throw new InvalidOperationException();
        });
        await client.Invoking(Value => Value.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")])).Should().ThrowAsync<ChatGptException>();
        dispatched.Should().BeFalse();
    }

    private static readonly Guid ConnectionId = Guid.Parse("cb8c5a00-985c-4504-9781-deb1a836a500");

    [Fact]
    public async Task FunctionCalls_ReplayToolOutput_WithoutReferencingStoredResponses()
    {
        var store = await SeedAsync();
        using var handler = new ToolResponsesHandler();
        using var http = new HttpClient(handler);
        using var client = new ChatGptChatClient(store, "owner", ConnectionId, "gpt-6.1-sol", Credential =>
            new ResponsesClient(new ApiKeyCredential(Credential), new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
            }).AsIChatClient("gpt-6.1-sol")).AsBuilder().UseFunctionInvocation().Build();
        var invoked = 0;
        var function = AIFunctionFactory.Create(() => { invoked++; return 42; }, "GetAnswer");
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Use the tool")], new ChatOptions { Tools = [function] });
        response.Text.Should().Be("Hello");
        invoked.Should().Be(1);
        handler.Requests.Should().Be(2);
    }

    private sealed class ToolResponsesHandler : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Requests++;
            var body = await Request.Content!.ReadAsStringAsync(Token);
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
            document.RootElement.TryGetProperty("previous_response_id", out _).Should().BeFalse();
            if (Requests == 2)
            {
                body.Should().Contain("function_call_output").And.Contain("call_test").And.Contain("42");
                return MessageResponse(false);
            }
            var stream = """
                event: response.output_item.added
                data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"fc_test","type":"function_call","status":"in_progress","name":"GetAnswer","call_id":"call_test","arguments":""}}

                event: response.function_call_arguments.delta
                data: {"type":"response.function_call_arguments.delta","sequence_number":2,"output_index":0,"item_id":"fc_test","delta":"{}"}

                event: response.output_item.done
                data: {"type":"response.output_item.done","sequence_number":3,"output_index":0,"item":{"id":"fc_test","type":"function_call","status":"completed","name":"GetAnswer","call_id":"call_test","arguments":"{}"}}

                event: response.completed
                data: {"type":"response.completed","sequence_number":4,"response":{"id":"resp_tool","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol","output":[{"id":"fc_test","type":"function_call","status":"completed","name":"GetAnswer","call_id":"call_test","arguments":"{}"}]}}

                """ + "\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
    private async Task<ChatGptConnectionStore> SeedAsync()
    {
        var database = new IntegrationDatabase(Fixture.ConnectionString, Convert.ToBase64String(new byte[32]));
        await using var connection = await database.OpenAsync(default);
        await using var create = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.chatgpt_connections(id uuid PRIMARY KEY, actor_id text, credentials text, version bigint DEFAULT 1);
            DELETE FROM app.chatgpt_connections;
            INSERT INTO app.chatgpt_connections(id, actor_id, credentials) VALUES(@id, 'owner', @credentials);
            """, connection);
        var credential = new ChatGptCredentials("oaiapp_test", "subject", null, "", "test-access", "refresh", ChatGptOAuth.Scope, DateTimeOffset.UtcNow.AddHours(1));
        create.Parameters.AddWithValue("id", ConnectionId);
        create.Parameters.AddWithValue("credentials", PostgresConfigurationCrypto.Encrypt(JsonSerializer.Serialize(credential), "ChatGPT", ConnectionId.ToString(), database.EncryptionKey));
        await create.ExecuteNonQueryAsync();
        return new(database, new ChatGptOAuth(new HttpClient()));
    }

    private sealed class ResponsesHandler(bool Interrupted) : HttpMessageHandler
    {
        public bool Called;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Called = true;
            Request.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/responses");
            Request.Headers.Authorization!.Parameter.Should().Be("test-access");
            using var document = JsonDocument.Parse(await Request.Content!.ReadAsStringAsync(Token));
            document.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("model").GetString().Should().Be("gpt-6.1-sol");
            document.RootElement.TryGetProperty("previous_response_id", out _).Should().BeFalse();
            return MessageResponse(Interrupted);
        }
    }

    private static HttpResponseMessage MessageResponse(bool Interrupted)
    {
            var events = """
                event: response.output_text.delta
                data: {"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_test","output_index":0,"content_index":0,"delta":"Hello","logprobs":[]}

                """ + "\n";
            if (!Interrupted) events += """
                event: response.completed
                data: {"type":"response.completed","sequence_number":2,"response":{"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol","output":[{"id":"msg_test","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}}

                """ + "\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
    }
}
#pragma warning restore OPENAI001
