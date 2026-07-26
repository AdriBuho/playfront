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
        // Aplica el color de acento guardado (o el verde por defecto) ANTES de crear la ventana,
        // para que los recursos del tema (AccentBrush, *Shadow, ...) existan cuando el XAML los
        // resuelva por DynamicResource - si no, habria un parpadeo sin halos al arrancar.
        AccentTheme.Apply(this, Color.Parse(AccentTheme.LoadSavedHex()));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}