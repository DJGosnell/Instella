using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class InMemoryRegistryTests
{
    [Test]
    public void Set_then_Get_roundtrips_string()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "Software\\Example", "Name", InstellaRegistryValueKind.String, "hello");

        Assert.That(reg.Get(RegistryHive.CurrentUser, "Software\\Example", "Name"), Is.EqualTo("hello"));
    }

    [Test]
    public void Set_overwrites_existing_value()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "K", "V", InstellaRegistryValueKind.String, "one");
        reg.Set(RegistryHive.CurrentUser, "K", "V", InstellaRegistryValueKind.String, "two");

        Assert.That(reg.Get(RegistryHive.CurrentUser, "K", "V"), Is.EqualTo("two"));
        Assert.That(reg.Snapshot().Count, Is.EqualTo(1));
    }

    [Test]
    public void Hives_are_isolated()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "K", "V", InstellaRegistryValueKind.String, "user");
        reg.Set(RegistryHive.LocalMachine, "K", "V", InstellaRegistryValueKind.String, "machine");

        Assert.That(reg.Get(RegistryHive.CurrentUser, "K", "V"), Is.EqualTo("user"));
        Assert.That(reg.Get(RegistryHive.LocalMachine, "K", "V"), Is.EqualTo("machine"));
        Assert.That(reg.Snapshot().Count, Is.EqualTo(2));
    }

    [Test]
    public void Key_paths_match_case_insensitively()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "Software\\Example", "Name", InstellaRegistryValueKind.String, "x");

        Assert.That(reg.Get(RegistryHive.CurrentUser, "SOFTWARE\\EXAMPLE", "Name"), Is.EqualTo("x"));
        Assert.That(reg.Contains(RegistryHive.CurrentUser, "software\\example"), Is.True);
    }

    [Test]
    public void Delete_removes_named_value()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "K", "V", InstellaRegistryValueKind.String, "x");

        Assert.That(reg.Delete(RegistryHive.CurrentUser, "K", "V"), Is.True);
        Assert.That(reg.Get(RegistryHive.CurrentUser, "K", "V"), Is.Null);
        Assert.That(reg.Contains(RegistryHive.CurrentUser, "K"), Is.False);
    }

    [Test]
    public void Delete_returns_false_when_value_absent()
    {
        var reg = new InMemoryRegistry();
        Assert.That(reg.Delete(RegistryHive.CurrentUser, "K", "V"), Is.False);
    }

    [Test]
    public void DeleteKey_removes_every_value()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "K", "V1", InstellaRegistryValueKind.String, "a");
        reg.Set(RegistryHive.CurrentUser, "K", "V2", InstellaRegistryValueKind.DWord, 42);
        reg.Set(RegistryHive.CurrentUser, "Other", "V", InstellaRegistryValueKind.String, "keep");

        Assert.That(reg.DeleteKey(RegistryHive.CurrentUser, "K"), Is.True);
        Assert.That(reg.Contains(RegistryHive.CurrentUser, "K"), Is.False);
        Assert.That(reg.Get(RegistryHive.CurrentUser, "Other", "V"), Is.EqualTo("keep"));
    }

    [Test]
    public void Snapshot_surfaces_full_entries()
    {
        var reg = new InMemoryRegistry();
        reg.Set(RegistryHive.CurrentUser, "K", "A", InstellaRegistryValueKind.DWord, 1);
        reg.Set(RegistryHive.LocalMachine, "L", "B", InstellaRegistryValueKind.String, "s");

        var snapshot = reg.Snapshot();
        Assert.That(snapshot.Count, Is.EqualTo(2));
        Assert.That(snapshot.Any(r => r.Hive == RegistryHive.CurrentUser && r.Kind == InstellaRegistryValueKind.DWord), Is.True);
        Assert.That(snapshot.Any(r => r.Hive == RegistryHive.LocalMachine && r.Value is string s && s == "s"), Is.True);
    }
}
