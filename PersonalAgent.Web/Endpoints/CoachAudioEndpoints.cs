using PersonalAgent.Web.Extensions;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Endpoints;

internal static class CoachAudioEndpoints
{
    public static void MapCoachAudio(this WebApplication App) =>
        App.MapGet("/media/coach-checkins/{UploadId:guid}/audio", async (HttpContext Context, Guid UploadId, string ProfileId, PersonalAgentClient Client) =>
        {
            using var Response = await Client.GetCoachAudioAsync(UploadId, ProfileId, Context.User, Context.Request.Headers.Range, Context.RequestAborted);
            Context.Response.StatusCode = (int)Response.StatusCode;
            Context.Response.Headers.CacheControl = "private, no-store";
            Context.Response.Headers.XContentTypeOptions = "nosniff";
            if (Response.Content.Headers.ContentRange is { } Range) Context.Response.Headers.ContentRange = Range.ToString();
            if (!Response.IsSuccessStatusCode) return;
            Context.Response.ContentType = Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            Context.Response.ContentLength = Response.Content.Headers.ContentLength;
            Context.Response.Headers.AcceptRanges = "bytes";
            await Response.Content.CopyToAsync(Context.Response.Body, Context.RequestAborted);
        }).RequireAuthorization(ServiceCollectionAuthenticationExtensions.CoachTranscriptPolicy);
}
