using Microsoft.Extensions.Logging;
using System.Net.Http;
using Microsoft.Maui.LifecycleEvents;
using Plugin.Firebase.Core.Platforms.Android;
using PersonalAgent.Mobile.Configuration;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;

namespace PersonalAgent.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android =>
                android.OnCreate((activity, _) =>
                    CrossFirebase.Initialize(activity, () => activity)));
#endif
        });

        var options = new MobileAppOptions
        {
            ApiBaseUrl = ResolveString("PERSONAL_AGENT_API_BASE_URL", "http://127.0.0.1:5100"),
            WebAppUrl = ResolveString("PERSONAL_AGENT_WEB_BASE_URL", "http://127.0.0.1:5100"),
            InternalApiKey = ResolveString("INTERNAL_API_KEY", "dev-internal-api-key"),
            ProfileId = ResolveString("PERSONAL_AGENT_PROFILE_ID", "mobile-dev"),
            AndroidChannelId = ResolveString("ANDROID_PUSH_CHANNEL_ID", "agent-approval-high"),
            EnableLocalApprovalShortcut = ResolveBool("ENABLE_LOCAL_APPROVAL_SHORTCUT", false)
        };

        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        builder.Services.AddSingleton<NotificationRoutingService>();
        builder.Services.AddSingleton<FirebaseCloudMessagingBridge>();
        builder.Services.AddSingleton<IPushTokenProvider, PushTokenProvider>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainPage>();

        builder.Services.AddHttpClient<PersonalAgentApiClient>((sp, client) =>
            {
                var appOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MobileAppOptions>>().Value;
                client.BaseAddress = new Uri(EnsureTrailingSlash(appOptions.ApiBaseUrl));
                client.Timeout = TimeSpan.FromSeconds(12);
                if (!string.IsNullOrWhiteSpace(appOptions.InternalApiKey))
                    client.DefaultRequestHeaders.Add("X-Internal-Api-Key", appOptions.InternalApiKey);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
#if DEBUG
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif
                return handler;
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static string ResolveString(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

    private static bool ResolveBool(string key, bool fallback)
    {
        var rawValue = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(rawValue, out var parsed) ? parsed : fallback;
    }
}
