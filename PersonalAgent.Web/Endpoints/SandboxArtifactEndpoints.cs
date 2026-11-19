using PersonalAgent.Web.Extensions;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Endpoints;

internal static class SandboxArtifactEndpoints
{
    public static void MapSandboxArtifacts(this WebApplication App)
    {
        App.MapGet("/automations/{id:guid}/runs/{run:guid}/artifacts/{step:int}", async (HttpContext Context, Guid id,
            Guid run, int step, string profileId, int? file, bool? preview, PersonalAgentClient Client, CancellationToken Token) =>
        {
            using var Response = await Client.DownloadSandboxArtifactAsync(id, run, step, file, profileId, Context.User, Token, preview == true);
            Context.Response.StatusCode = (int)Response.StatusCode;
            Context.Response.Headers.CacheControl = "private, no-store";
            if (!Response.IsSuccessStatusCode) return;
            Context.Response.Headers.XContentTypeOptions = "nosniff";
            Context.Response.ContentType = Response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            Context.Response.Headers.ContentDisposition = Response.Content.Headers.ContentDisposition?.ToString() ?? (Context.Response.ContentType == "image/png" ? "inline" : "attachment");
            if (Context.Response.ContentType == "image/png") Context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            Context.Response.ContentLength = Response.Content.Headers.ContentLength;
            await Response.Content.CopyToAsync(Context.Response.Body, Token);
        }).RequireAuthorization(ServiceCollectionAuthenticationExtensions.CoachTranscriptPolicy);
    }
}
