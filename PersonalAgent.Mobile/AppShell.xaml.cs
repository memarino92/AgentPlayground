namespace PersonalAgent.Mobile;

public partial class AppShell : Shell
{
    public AppShell(MainPage mainPage)
    {
        InitializeComponent();
        Items.Add(new ShellContent
        {
            Title = "Chat",
            ContentTemplate = new DataTemplate(() => mainPage),
            Route = nameof(MainPage)
        });
    }
}
