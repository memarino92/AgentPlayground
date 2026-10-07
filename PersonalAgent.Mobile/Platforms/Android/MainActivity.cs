using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Extensions.Options;
using Plugin.Firebase.CloudMessaging;

using PersonalAgent.Mobile.Configuration;

namespace PersonalAgent.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var channelId = IPlatformApplication.Current!.Services.GetRequiredService<IOptions<MobileAppOptions>>().Value.AndroidChannelId;
        FirebaseCloudMessagingImplementation.ChannelId = channelId;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(channelId, "Agent approvals", NotificationImportance.High)
            {
                Description = "Requests to review and approve agent actions"
            };
            ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
        }

        if (Intent is not null) FirebaseCloudMessagingImplementation.OnNewIntent(Intent);

        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1001);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) FirebaseCloudMessagingImplementation.OnNewIntent(intent);
    }
}
