namespace PersonalAgent.Web.Endpoints;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (string? returnUrl, string? provider) =>
        {
            var scheme = string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase) ? "Google" : "GitHub";
            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = returnUrl ?? "/",
                    IsPersistent = true,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                },
                [scheme]
            );
        }).WithName("Login");

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/signed-out");
        }).WithName("Logout");

        app.MapGet("/signin-github", () => Results.Redirect("/"))
            .WithName("SignInCallback");
        app.MapGet("/signin-google", () => Results.Redirect("/"))
            .WithName("GoogleSignInCallback");
    }
}
