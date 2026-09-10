using System.Security.Claims;
using AgentPlayground.Contracts.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PersonalAgent.Web.Extensions;

namespace PersonalAgent.Web.Development;

internal static class SyntheticAuthentication
{
    public static void AddSyntheticAuthentication(this IServiceCollection Services)
    {
        Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(Options =>
        {
            Options.LoginPath = "/login";
            Options.AccessDeniedPath = "/access-denied";
            Options.Cookie.Name = "SyntheticDemo.Auth";
            Options.Cookie.HttpOnly = true;
            Options.Cookie.SameSite = SameSiteMode.Strict;
            Options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            Options.ExpireTimeSpan = TimeSpan.FromHours(4);
        });
        Services.AddAuthorization(Options =>
        {
            Options.AddPolicy(ServiceCollectionAuthenticationExtensions.OwnerPolicy, Policy => Policy.RequireRole("Owner"));
            Options.AddPolicy(ServiceCollectionAuthenticationExtensions.CoachTranscriptPolicy, Policy => Policy.RequireRole("Owner", "Coach"));
        });
    }

    public static void MapSyntheticAuthentication(this WebApplication App)
    {
        if (!SyntheticEnvironment.IsEnabled(App.Configuration, App.Environment)) throw new InvalidOperationException("Synthetic sign-in is disabled.");
        App.MapGet("/login", (HttpContext Context, IAntiforgery Antiforgery) =>
        {
            var Token = Antiforgery.GetAndStoreTokens(Context);
            return Results.Content($"""
                <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                <title>Synthetic garden demo</title><body style="font:18px system-ui;max-width:640px;margin:10vh auto;padding:24px">
                <h1>Synthetic garden demo</h1><p>Explore sample conversations and coaching notes. All AI responses are fixed demonstrations.</p>
                <form method="post" action="/demo/sign-in">
                <input type="hidden" name="{Token.FormFieldName}" value="{Token.RequestToken}">
                <label for="persona">Choose a sample account</label>
                <select id="persona" name="persona"><option value="owner">Demo owner</option><option value="coach">Assigned coach</option><option value="other">Other owner</option></select>
                <button type="submit">Sign in</button></form></body></html>
                """, "text/html");
        }).WithSummary("Choose a synthetic development account");
        App.MapPost("/demo/sign-in", async (HttpContext Context, IAntiforgery Antiforgery) =>
        {
            try { await Antiforgery.ValidateRequestAsync(Context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var Form = await Context.Request.ReadFormAsync(Context.RequestAborted);
            var Persona = Form["persona"].ToString();
            if (Persona is not ("owner" or "coach" or "other")) return Results.BadRequest();
            var Coach = Persona == "coach";
            var Actor = Persona == "other" ? "demo-other" : "demo-owner";
            var Claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, Coach ? "synthetic-coach" : Actor),
                new(ClaimTypes.Name, Coach ? "Synthetic coach" : Actor),
                new(ClaimTypes.Role, Coach ? "Coach" : "Owner"),
                new(ClaimTypes.Email, Coach ? "coach@example.test" : $"{Actor}@example.test")
            };
            if (!Coach) Claims.Add(new("urn:github:login", Actor));
            await Context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(Claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.LocalRedirect(Coach ? "/coach-transcripts" : "/");
        }).WithSummary("Sign in as a predefined synthetic account");
        App.MapPost("/logout", async (HttpContext Context) =>
        {
            await Context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });
    }
}
