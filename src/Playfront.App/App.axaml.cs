using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Playfront.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved accent (or the default green) BEFORE creating the window, so the theme
        // resources (AccentBrush, *Shadow, ...) exist by the time XAML resolves them through
        // DynamicResource - otherwise startup flashes once without the glows.
        AccentTheme.Apply(this, Color.Parse(AccentTheme.LoadSavedHex()));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}