using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;

    public App(MainPage mainPage, FirebaseCloudMessagingBridge firebaseCloudMessagingBridge)
    {
        InitializeComponent();
        _mainPage = mainPage;
        _ = firebaseCloudMessagingBridge;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_mainPage);
}
