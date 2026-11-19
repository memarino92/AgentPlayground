using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PersonalAgent.Mobile;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "personalagentmobile", DataHost = "chat")]
public class ChatCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity;
