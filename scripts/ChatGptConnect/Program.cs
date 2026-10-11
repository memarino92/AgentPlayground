using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PersonalAgent.Integrations.ChatGpt;

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var instance)
    || !string.IsNullOrEmpty(instance.UserInfo) || instance.AbsolutePath != "/" || instance.Query.Length > 0 || instance.Fragment.Length > 0
    || instance.Scheme != "https" && !(instance.Scheme == "http" && instance.IsLoopback))
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/ChatGptConnect -- https://your-instance.example");
    return 1;
}
Console.Write("Pairing code from Settings: ");
var pairingCode = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(pairingCode)) return 1;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
var endpoint = new Uri(instance, "/chatgpt/handshake");
try
{
    using var prepared = await http.PostAsJsonAsync(endpoint, new ChatGptHandshake(pairingCode), timeout.Token);
    if (!prepared.IsSuccessStatusCode) throw new ChatGptException("Pairing code expired or is unavailable. Start again in Settings.");
    var attempt = await prepared.Content.ReadFromJsonAsync<ChatGptAuthorization>(timeout.Token)
        ?? throw new ChatGptException("The instance returned an invalid pairing.");
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders(); // Callback URLs contain one-time authorization codes.
    builder.WebHost.UseUrls("http://127.0.0.1:1455");
    await using var listener = builder.Build();
    var received = new TaskCompletionSource<IQueryCollection>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.MapGet("/auth/callback", (HttpContext Context) =>
    {
        Context.Response.Headers.CacheControl = "no-store";
        var query = Context.Request.Query;
        if (query["state"].Count != 1 || query["state"] != attempt.State
            || query["code"].Count > 1 || query["client_id"].Count > 1 || query["error"].Count > 1)
            return Results.BadRequest("Invalid sign-in callback.");
        return received.TrySetResult(query)
            ? Results.Text("Callback received. Return to the sign-in helper.")
            : Results.BadRequest("This callback was already received.");
    });
    await listener.StartAsync(timeout.Token);
    // Credentials and authorization URLs are deliberately never printed.
    Process.Start(new ProcessStartInfo(ChatGptOAuth.AuthorizeUrl(attempt)) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    Console.WriteLine("Finish authorization in your browser.");
    var callbackQuery = await received.Task.WaitAsync(timeout.Token);
    await listener.StopAsync(timeout.Token);
    var credentials = await new ChatGptOAuth(http).ExchangeAsync(attempt, callbackQuery["state"].ToString(),
        callbackQuery["code"].ToString(), callbackQuery.ContainsKey("client_id") ? callbackQuery["client_id"].ToString() : null,
        callbackQuery.ContainsKey("error") ? callbackQuery["error"].ToString() : null, timeout.Token);
    using var completed = await http.PostAsJsonAsync(endpoint, new ChatGptHandshake(pairingCode, credentials), timeout.Token);
    if (!completed.IsSuccessStatusCode) throw new ChatGptException("The instance could not save the connection. Start again in Settings.");
    Console.WriteLine(ChatGptOAuth.PlanEnabled(credentials.Scope)
        ? "ChatGPT plan connected. Refresh Settings and select a ChatGPT model."
        : "Account connected without plan permission. Reconnect to authorize ChatGPT plan use.");
    return 0;
}
catch (ChatGptException exception) { Console.Error.WriteLine(exception.Message); }
catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("Sign-in could not finish. Check the instance URL, network and availability of loopback port 1455, then create a fresh pairing in Settings.");
}
return 1;
