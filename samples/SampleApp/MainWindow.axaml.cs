using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Instella.Core.Update;
using Instella.Sdk;

namespace QuickNotes;

public partial class MainWindow : Window
{
    private string? _currentFilePath;
    private bool _isModified;
    private string _savedContent = "";

    public MainWindow() : this(null) { }

    public MainWindow(string? fileToOpen)
    {
        InitializeComponent();

        // Wire up events
        NewButton.Click += OnNewClick;
        OpenButton.Click += OnOpenClick;
        SaveButton.Click += OnSaveClick;
        SaveAsButton.Click += OnSaveAsClick;
        CheckUpdatesButton.Click += OnCheckUpdatesClick;
        Editor.TextChanged += OnEditorTextChanged;

        // Keyboard shortcuts
        KeyDown += OnKeyDown;

        // Load file if provided (file association)
        if (!string.IsNullOrEmpty(fileToOpen) && File.Exists(fileToOpen))
        {
            LoadFile(fileToOpen);
        }

        UpdateStatusBar();

        // Show the running version, so an update is visible at a glance.
        AppTitleText.Text = $"QuickNotes {AppVersion}";

        // Set by Program on the first start after an update (InstellaClient.PostUpdate).
        if (Program.UpdatedFrom is { } previous)
            UpdateStatusText.Text = $"Updated from {previous}";
    }

    /// <summary>The app's version (Major.Minor.Build), from the assembly.</summary>
    private static string AppVersion =>
        typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            // Never throws for network or server errors: they come back as Status == Failed.
            var result = await InstellaClient.CheckForUpdateAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is { } update:
                    // Ask first: the version, what changed and how much to download.
                    if (!await UpdateDialog.AskAsync(this, update))
                    {
                        UpdateStatusText.Text = $"QuickNotes {update.Version} is available";
                        break;
                    }
                    if (_isModified && !ConfirmDiscard())
                        break;
                    UpdateStatusText.Text = $"Updating to {update.Version}...";
                    // Starts the installed instella stub with --update and exits this process;
                    // the updater swaps the files, shows the result, and restarts QuickNotes.
                    await InstellaClient.LaunchUpdaterAndExitAsync(update);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = "QuickNotes is up to date";
                    break;
                case UpdateCheckStatus.NotInstalled:
                    // Started from the IDE or a build folder, not from an installation.
                    UpdateStatusText.Text = "Updates are available only in an installed copy";
                    break;
                default:
                    UpdateStatusText.Text = $"Update check failed: {result.Error}";
                    break;
            }
        }
        catch (Exception ex)
        {
            // LaunchUpdaterAndExitAsync throws when the stub cannot be started.
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.N:
                    OnNewClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.O:
                    OnOpenClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    OnSaveAsClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S:
                    OnSaveClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (_isModified && !ConfirmDiscard())
            return;

        Editor.Text = "";
        _currentFilePath = null;
        _savedContent = "";
        _isModified = false;
        UpdateStatusBar();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_isModified && !ConfirmDiscard())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Note",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("QuickNotes Files") { Patterns = new[] { "*.qnote", "*.qn" } },
                new FilePickerFileType("Text Files") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            LoadFile(path);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            OnSaveAsClick(sender, e);
            return;
        }

        await SaveFile(_currentFilePath);
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Note",
            DefaultExtension = "qnote",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("QuickNotes Document") { Patterns = new[] { "*.qnote" } },
                new FilePickerFileType("QuickNotes File") { Patterns = new[] { "*.qn" } },
                new FilePickerFileType("Text File") { Patterns = new[] { "*.txt" } }
            }
        });

        if (file != null)
        {
            await SaveFile(file.Path.LocalPath);
        }
    }

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        _isModified = Editor.Text != _savedContent;
        UpdateStatusBar();
    }

    private void LoadFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            Editor.Text = content;
            _currentFilePath = path;
            _savedContent = content;
            _isModified = false;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            // Show error (simplified - in production use proper dialog)
            Title = $"QuickNotes - Error: {ex.Message}";
        }
    }

    private async Task SaveFile(string path)
    {
        try
        {
            await File.WriteAllTextAsync(path, Editor.Text ?? "");
            _currentFilePath = path;
            _savedContent = Editor.Text ?? "";
            _isModified = false;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            Title = $"QuickNotes - Error: {ex.Message}";
        }
    }

    private bool ConfirmDiscard()
    {
        // Simplified confirmation - in production use proper dialog
        // For this sample, we'll just allow discarding
        return true;
    }

    private void UpdateStatusBar()
    {
        // File path
        FilePathText.Text = string.IsNullOrEmpty(_currentFilePath)
            ? "New Note"
            : Path.GetFileName(_currentFilePath);

        // Character count
        var charCount = Editor.Text?.Length ?? 0;
        CharCountText.Text = $"{charCount:N0} characters";

        // Word count
        var wordCount = string.IsNullOrWhiteSpace(Editor.Text)
            ? 0
            : WordCountRegex().Matches(Editor.Text).Count;
        WordCountText.Text = $"{wordCount:N0} words";

        // Modified indicator
        ModifiedText.Text = _isModified ? "Modified" : "";

        // Window title
        var fileName = string.IsNullOrEmpty(_currentFilePath)
            ? "New Note"
            : Path.GetFileName(_currentFilePath);
        Title = _isModified
            ? $"*{fileName} - QuickNotes"
            : $"{fileName} - QuickNotes";
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordCountRegex();
}
