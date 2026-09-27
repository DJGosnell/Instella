using Instella.Core.Platform;
using Instella.Core.Platform.Windows;
using Microsoft.Win32;
using NUnit.Framework;

namespace Instella.Core.Tests;

[TestFixture]
public class PathListEditorTests
{
    [Test]
    public void AddThenRemove_KeepsUnexpandedEntriesVerbatim()
    {
        const string original = @"%JAVA_HOME%\bin;%SystemRoot%\system32;C:\Tools";
        var added = PathListEditor.Add(original, @"C:\Users\a\AppData\Local\Programs\QuickNotes");
        Assert.That(added, Is.EqualTo(original + @";C:\Users\a\AppData\Local\Programs\QuickNotes"));

        var removed = PathListEditor.Remove(added!, @"C:\Users\a\AppData\Local\Programs\QuickNotes\");
        Assert.That(removed, Is.EqualTo(original));
    }

    [Test]
    public void Add_IsIdempotentAndCaseInsensitive()
    {
        Assert.That(PathListEditor.Add(@"C:\Tools\;D:\x", @"c:\tools"), Is.Null);
    }

    [Test]
    public void Remove_MatchesWholeEntriesOnly()
    {
        // Substring matching would strip C:\App from C:\AppData\bin as well.
        const string path = @"C:\AppData\bin;C:\App;C:\App\sub";
        Assert.That(PathListEditor.Remove(path, @"C:\App"), Is.EqualTo(@"C:\AppData\bin;C:\App\sub"));
    }

    [Test]
    public void Remove_AbsentEntry_ReportsNoChange()
    {
        Assert.That(PathListEditor.Remove(@"C:\a;C:\b", @"C:\c"), Is.Null);
    }
}

[TestFixture]
[Platform("Win")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsPlatformServicesTests
{
    [TestCase(@"C:\App\QuickNotes.exe", @"C:\App", true)]
    [TestCase(@"C:\App\sub\tool.exe", @"C:\App\", true)]
    [TestCase(@"C:\AppData\evil.exe", @"C:\App", false)]
    [TestCase(@"C:\Apps.exe", @"C:\App", false)]
    public void IsUnderDirectory_RequiresSeparatorBoundary(string path, string dir, bool expected)
    {
        Assert.That(WindowsPlatformServices.IsUnderDirectory(path, dir), Is.EqualTo(expected));
    }

    private RegistryKey _root = null!;
    private string _rootPath = null!;

    [SetUp]
    public void SetUp()
    {
        _rootPath = $@"Software\InstellaTests\{Guid.NewGuid():N}";
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
    }

    [TearDown]
    public void TearDown()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
    }

    private static FileAssociationInfo Info(string appId) =>
        new(".qnote", "QuickNotes note", appId, @"C:\App\QuickNotes.exe", IconPath: null);

    [Test]
    public void Association_WeCreated_IsDeletedOnUnregister()
    {
        WindowsPlatformServices.RegisterFileAssociation(_root, Info("com.example.quicknotes"));
        using (var ext = _root.OpenSubKey(@"Software\Classes\.qnote"))
        {
            Assert.That(ext!.GetValue(null), Is.EqualTo("com.example.quicknotes.qnote"));
            Assert.That(ext.GetValue(WindowsPlatformServices.OwnerValueName), Is.EqualTo("com.example.quicknotes"));
        }

        WindowsPlatformServices.UnregisterFileAssociation(_root, ".qnote", "com.example.quicknotes");
        Assert.That(_root.OpenSubKey(@"Software\Classes\.qnote"), Is.Null);
        Assert.That(_root.OpenSubKey(@"Software\Classes\com.example.quicknotes.qnote"), Is.Null);
    }

    [Test]
    public void Association_OnSomeoneElsesKey_RestoresTheirDefault()
    {
        using (var ext = _root.CreateSubKey(@"Software\Classes\.qnote"))
        {
            ext.SetValue(null, "Other.App.qnote");
            ext.SetValue("Content Type", "text/plain");
        }

        WindowsPlatformServices.RegisterFileAssociation(_root, Info("com.example.quicknotes"));
        WindowsPlatformServices.UnregisterFileAssociation(_root, ".qnote", "com.example.quicknotes");

        using var after = _root.OpenSubKey(@"Software\Classes\.qnote");
        Assert.That(after, Is.Not.Null, "a key we did not create must survive uninstall");
        Assert.That(after!.GetValue(null), Is.EqualTo("Other.App.qnote"));
        Assert.That(after.GetValue("Content Type"), Is.EqualTo("text/plain"));
        Assert.That(after.GetValue(WindowsPlatformServices.PreviousDefaultValueName), Is.Null);
        using var openWith = after.OpenSubKey("OpenWithProgids");
        Assert.That(openWith?.GetValueNames() ?? [], Does.Not.Contain("com.example.quicknotes.qnote"));
    }

    [Test]
    public void Association_OtherAppsUnregister_DoesNotTouchOurKey()
    {
        WindowsPlatformServices.RegisterFileAssociation(_root, Info("com.example.quicknotes"));
        WindowsPlatformServices.UnregisterFileAssociation(_root, ".qnote", "com.other.app");

        using var ext = _root.OpenSubKey(@"Software\Classes\.qnote");
        Assert.That(ext!.GetValue(null), Is.EqualTo("com.example.quicknotes.qnote"));
    }
}

[TestFixture]
[Platform("Win")]
public class ShortcutIconTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AnIconThatIsNotOnDiskYet_IsUsed_NotTheExe()
    {
        // On a first install the shortcut is made while app.ico is still staged, so an
        // existence check would fall back to the exe icon.
        var icon = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"), ".instella", "app.ico");

        Assert.That(WindowsShortcutCreator.IconLocationFor(icon, @"C:\Apps\QN\QuickNotes.exe"), Is.EqualTo(icon));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(@"..\..\Assets\icon.ico")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NoIcon_OrABuildMachinePath_UsesTheExe(string? icon)
    {
        Assert.That(WindowsShortcutCreator.IconLocationFor(icon, @"C:\Apps\QN\QuickNotes.exe"), Is.EqualTo(@"C:\Apps\QN\QuickNotes.exe"));
    }
}
