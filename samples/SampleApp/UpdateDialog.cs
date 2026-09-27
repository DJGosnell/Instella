using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Instella.Core.Update;

namespace QuickNotes;

/// <summary>
/// Asks before updating: the version, the changelog and the download size, with "Update now"
/// and "Later". The SDK reports all of it in <see cref="UpdateInfo"/>; showing it is the app's job.
/// </summary>
internal sealed class UpdateDialog : Window
{
    private UpdateDialog(UpdateInfo update)
    {
        Title = "Update available";
        Width = 460;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var size = update.PatchAvailable && update.PatchSize is { } patch ? patch : update.FullSize;
        var updateNow = new Button { Content = "Update now", IsDefault = true };
        var later = new Button { Content = "Later", IsCancel = true };
        updateNow.Click += (_, _) => Close(true);
        later.Click += (_, _) => Close(false);

        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                Top(new TextBlock
                {
                    Text = $"QuickNotes {update.Version} is available",
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                }),
                Top(new TextBlock
                {
                    Text = $"Download: {(size / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture)} MB",
                    Margin = new Avalonia.Thickness(0, 4, 0, 8),
                }),
                Bottom(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 12, 0, 0),
                    Children = { updateNow, later },
                }),
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(update.Changelog) ? "No release notes." : update.Changelog,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                },
            },
        };
    }

    /// <summary>Shows the dialog; true when the user chose "Update now".</summary>
    public static Task<bool> AskAsync(Window owner, UpdateInfo update) =>
        new UpdateDialog(update).ShowDialog<bool>(owner);

    private static Control Top(Control c)
    {
        DockPanel.SetDock(c, Dock.Top);
        return c;
    }

    private static Control Bottom(Control c)
    {
        DockPanel.SetDock(c, Dock.Bottom);
        return c;
    }
}
