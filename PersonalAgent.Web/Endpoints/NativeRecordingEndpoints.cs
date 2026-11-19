using System.Security.Claims;

using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Endpoints;

internal static class NativeRecordingEndpoints
{
    public static void MapNativeRecordings(this WebApplication App) =>
        App.MapPost("/mobile/recordings/{Operation}/{Id:guid}", TransferAsync);

    private static async Task<IResult> TransferAsync(string Operation, Guid Id, string ProfileId, HttpContext Context,
        MobileChatGrant Grants, PersonalAgentClient Client, CancellationToken Token)
    {
        Context.Response.Headers.CacheControl = "no-store";
        var user = Grants.Read(Context.Request.Headers.Authorization.ToString());
        if (user is null) return Results.Unauthorized();
        var owner = user.IsInRole("Owner");
        Client.UseMobileAccount(user);
        try
        {
            if (owner) ProfileId = user.FindFirstValue("urn:github:login")!;
            else
            {
                if (Operation is not ("audio" or "transcript")) return Results.StatusCode(403);
                var assignments = await Client.GetAssignedProfilesAsync($"google:{user.FindFirstValue(ClaimTypes.NameIdentifier)}", user.FindFirstValue(ClaimTypes.Email)!, Token);
                if (!assignments.Contains(ProfileId, StringComparer.OrdinalIgnoreCase)) return Results.StatusCode(403);
            }
            switch (Operation)
            {
                case "audio":
                    var response = await Client.GetCoachAudioAsync(Id, ProfileId, user, null, Token);
                    Context.Response.RegisterForDispose(response);
                    if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
                    return Results.Stream(await response.Content.ReadAsStreamAsync(Token), response.Content.Headers.ContentType?.MediaType ?? "audio/mp4");
                case "transcript":
                    var transcript = await Client.DownloadCoachCheckinTranscriptAsync(Id, ProfileId);
                    return Results.File(transcript.Bytes, "text/plain");
                case "delete": await Client.DeleteCoachAudioAsync(Id, ProfileId); break;
                case "attach": await Client.AttachCoachAudioAsync(Id, ProfileId, Context.Request.Body, "audio/mp4", Token); break;
                case "upload":
                    var name = Path.GetFileName(Context.Request.Query["name"].ToString());
                    if (!name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
                    return await Client.UploadMobileRecordingAsync(ProfileId, name, Context.Request.Body, Token);
                default: return Results.NotFound();
            }
            return Results.Ok(true);
        }
        catch (HttpRequestException error) { return Results.StatusCode((int?)error.StatusCode ?? 502); }
    }
}
