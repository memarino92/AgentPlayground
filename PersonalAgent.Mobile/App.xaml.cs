namespace PersonalAgent.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider Services)
    {
        _services = Services;
        InitializeComponent();
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
