using AndroidApplication = Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application;
using WindowSoftInputModeAdjust = Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust;

namespace PersonalAgent.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider Services)
    {
        _services = Services;
        InitializeComponent();
        AndroidApplication.SetWindowSoftInputModeAdjust(this, WindowSoftInputModeAdjust.Resize);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = _services.GetRequiredService<MainPage>();
        var window = new Window(new AppShell(page));
        window.Activated += async (_, _) => await page.SetActiveAsync(true);
        window.Stopped += async (_, _) => await page.SetActiveAsync(false);
        return window;
    }
}
