using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace QuickNotes;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? fileToOpen = null;

            if (desktop.Args?.Length > 0 && !desktop.Args[0].StartsWith('-'))
            {
                fileToOpen = desktop.Args[0];
            }

            desktop.MainWindow = new MainWindow(fileToOpen);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
