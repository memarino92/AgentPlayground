namespace PersonalAgent.Web.Endpoints;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
                ["GitHub"]
            )
        ).WithName("Login");

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignOutAsync("GitHub");
            return Results.Redirect("/");
        }).WithName("Logout");

        app.MapGet("/signin-github", () => Results.Redirect("/"))
            .WithName("SignInCallback");
    }
}
