using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class FakePlatformServicesTests
{
    [Test]
    public async Task WriteRegistryValue_routes_through_registry()
    {
        var registry = new InMemoryRegistry();
        var platform = new FakePlatformServices(registry);

        var ok = await platform.WriteRegistryValueAsync(
            RegistryHive.CurrentUser, "Software\\Example", "Name",
            InstellaRegistryValueKind.String, "test", perUser: true, default);

        Assert.That(ok.Success, Is.True);
        Assert.That(registry.Get(RegistryHive.CurrentUser, "Software\\Example", "Name"), Is.EqualTo("test"));
    }

    [Test]
    public async Task DeleteRegistryValue_routes_through_registry()
    {
        var registry = new InMemoryRegistry();
        registry.Set(RegistryHive.CurrentUser, "K", "V", InstellaRegistryValueKind.String, "x");
        var platform = new FakePlatformServices(registry);

        var ok = await platform.DeleteRegistryValueAsync(
            RegistryHive.CurrentUser, "K", "V", perUser: true, default);

        Assert.That(ok.Success, Is.True);
        Assert.That(registry.Get(RegistryHive.CurrentUser, "K", "V"), Is.Null);
    }

    [Test]
    public async Task DeleteRegistryValue_fails_when_value_absent()
    {
        var platform = new FakePlatformServices();
        var ok = await platform.DeleteRegistryValueAsync(
            RegistryHive.CurrentUser, "K", "V", perUser: true, default);
        Assert.That(ok.Success, Is.False);
    }

    [Test]
    public async Task DeleteRegistryKey_removes_entire_key()
    {
        var registry = new InMemoryRegistry();
        registry.Set(RegistryHive.CurrentUser, "K", "V1", InstellaRegistryValueKind.String, "a");
        registry.Set(RegistryHive.CurrentUser, "K", "V2", InstellaRegistryValueKind.DWord, 1);
        var platform = new FakePlatformServices(registry);

        var ok = await platform.DeleteRegistryKeyAsync(RegistryHive.CurrentUser, "K", perUser: true, default);
        Assert.That(ok.Success, Is.True);
        Assert.That(registry.Contains(RegistryHive.CurrentUser, "K"), Is.False);
    }

    [Test]
    public async Task Shortcut_and_PATH_calls_are_recorded()
    {
        var platform = new FakePlatformServices();

        await platform.CreateShortcutAsync(new ShortcutInfo(
            Name: "TestApp",
            TargetPath: "C:\\App\\test.exe",
            IconPath: null,
            Arguments: null,
            Location: ShortcutLocation.Desktop), default);

        await platform.AddToPathAsync("C:\\App", perUser: true, default);

        Assert.That(platform.Shortcuts.Count, Is.EqualTo(1));
        Assert.That(platform.Shortcuts[0].Name, Is.EqualTo("TestApp"));
        Assert.That(platform.PathEntries.Count, Is.EqualTo(1));
        Assert.That(platform.PathEntries[0].Directory, Is.EqualTo("C:\\App"));
    }

    [Test]
    public async Task UninstallEntry_calls_are_recorded()
    {
        var platform = new FakePlatformServices();

        await platform.RegisterUninstallEntryAsync(new UninstallEntryInfo(
            AppId: "com.test",
            DisplayName: "Test",
            DisplayVersion: "1.0",
            Publisher: "Acme",
            InstallLocation: "C:\\App",
            DisplayIcon: "C:\\App\\test.exe",
            UninstallCommand: "\"C:\\App\\instella.exe\" --uninstall",
            UrlInfoAbout: null,
            EstimatedSizeKb: 1024,
            PerUser: true), default);

        await platform.UnregisterUninstallEntryAsync("com.test", perUser: true, default);

        Assert.That(platform.UninstallEntries.Count, Is.EqualTo(1));
        Assert.That(platform.UninstallEntriesRemoved.Count, Is.EqualTo(1));
    }

    [Test]
    public void Platform_property_reflects_constructor_argument()
    {
        Assert.That(new FakePlatformServices().Platform, Is.EqualTo(TargetPlatform.Windows));
        Assert.That(new FakePlatformServices(TargetPlatform.Linux).Platform, Is.EqualTo(TargetPlatform.Linux));
        Assert.That(new FakePlatformServices(new InMemoryRegistry(), TargetPlatform.MacOS).Platform, Is.EqualTo(TargetPlatform.MacOS));
    }
}
