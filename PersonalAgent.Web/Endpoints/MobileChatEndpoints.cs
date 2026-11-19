using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Endpoints;

internal static class MobileChatEndpoints
{
    public static void MapMobileChat(this WebApplication App)
    {
        App.MapPost("/mobile/chat/{Id:guid}/stream", async (HttpContext Context, Guid Id, MobileChatRequest Request, MobileChatGrant Grants, PersonalAgentClient Client) =>
        {
            Context.Response.Headers.CacheControl = "no-store";
            var user = Grants.Read(Context.Request.Headers.Authorization.ToString());
            if (user is null) { Context.Response.StatusCode = 401; return; }
            var profile = user.IsInRole("Owner") ? user.FindFirst("urn:github:login")?.Value : Request.ProfileId;
            if (string.IsNullOrWhiteSpace(profile)) { Context.Response.StatusCode = 400; return; }
            using var response = await Client.MobileStreamAsync(Id, profile, Request.Message, user, Context.RequestAborted);
            Context.Response.StatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return;
            Context.Response.ContentType = "application/x-ndjson";
            await response.Content.CopyToAsync(Context.Response.Body, Context.RequestAborted);
        });
        App.MapPost("/mobile/chat/open", (HttpContext Context, MobileChatRequest Request, MobileChatGrant Grants, PersonalAgentClient Client) =>
            OpenAsync(Context, Request, Grants, Client));
    }

    private static async Task<IResult> OpenAsync(HttpContext Context, MobileChatRequest Request, MobileChatGrant Grants, PersonalAgentClient Client)
    {
        Context.Response.Headers.CacheControl = "no-store";
        var User = Grants.Read(Context.Request.Headers.Authorization.ToString());
        if (User is null) return Results.Unauthorized();
        var Profile = User.IsInRole("Owner") ? User.FindFirst("urn:github:login")?.Value : Request.ProfileId;
        if (string.IsNullOrWhiteSpace(Profile)) return Results.BadRequest();
        using var Response = await Client.OpenMobileConversationAsync(Profile, User, Context.RequestAborted);
        if (!Response.IsSuccessStatusCode) return Results.StatusCode((int)Response.StatusCode);
        return Results.Content(await Response.Content.ReadAsStringAsync(Context.RequestAborted), "application/json");
    }

    internal record MobileChatRequest(string? ProfileId, string? Message);
}
