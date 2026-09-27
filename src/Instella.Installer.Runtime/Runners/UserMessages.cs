using System;
using Instella.Installer.Runtime.Logging;
using Instella.Installer.Runtime.UI.Windows;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// What the installer tells the user outside a wizard window: errors, notices and yes/no
/// questions. A GUI installer has no console, so an error only written to stderr or the
/// log is invisible.
/// </summary>
internal interface IUserMessages
{
    /// <summary>Shows an error.</summary>
    void Error(string title, string text);

    /// <summary>Shows a notice.</summary>
    void Info(string title, string text);

    /// <summary>Asks a yes/no question; false when nobody can answer.</summary>
    bool Confirm(string title, string text);
}

/// <summary>
/// Windows and not <c>--silent</c>: message boxes. Otherwise stderr, and <see cref="Confirm"/>
/// answers no. Errors always go to stderr too, for a console that started the installer.
/// </summary>
internal sealed class UserMessages(bool silent) : IUserMessages
{
    /// <summary>
    /// Test assemblies set this once (a <c>SetUpFixture</c>): a message box nobody closes
    /// hangs the test run, so every <see cref="UserMessages"/> writes to stderr only.
    /// </summary>
    internal static bool DialogsDisabled { get; set; }

    private bool Visible => !silent && !DialogsDisabled && OperatingSystem.IsWindows();

    public void Error(string title, string text)
    {
        Console.Error.WriteLine($"error: {text}");
        if (Visible) Win32.MessageBoxW(0, text, title, MB.ICONERROR | MB.OK);
    }

    public void Info(string title, string text)
    {
        if (Visible) Win32.MessageBoxW(0, text, title, MB.ICONINFORMATION | MB.OK);
        else Console.Error.WriteLine(text);
    }

    public bool Confirm(string title, string text) =>
        Visible && Win32.MessageBoxW(0, text, title, MB.YESNO | MB.ICONQUESTION) == IDRESULT.YES;

    /// <summary><paramref name="text"/>, plus where the log is when there is one.</summary>
    public static string WithLog(string text) =>
        InstellaLogInitializer.CurrentFilePath is { } path ? $"{text}\n\nDetails are in the log: {path}" : text;
}
