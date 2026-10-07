using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_mainPage);
        window.Activated += async (_, _) => await _mainPage.SetActiveAsync(true);
        window.Stopped += async (_, _) => await _mainPage.SetActiveAsync(false);
        return window;
    }
}
