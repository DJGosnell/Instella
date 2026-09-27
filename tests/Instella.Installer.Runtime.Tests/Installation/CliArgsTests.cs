using System;
using System.IO;
using System.Reflection;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Covers the author-defined command line: typed AddCliFlag declaration, CliArgParser
/// coercion, MapsTo binding, reserved-flag rejection, unsupported-type
/// rejection.
/// </summary>
[TestFixture]
public sealed class CliArgsTests
{
    private static readonly Assembly RuntimeAsm = typeof(CliArgs).Assembly;
    private static readonly Type ParserType = RuntimeAsm.GetType("Instella.Installer.Runtime.Builders.CliArgParser", throwOnError: true)!;
    private static readonly Type SpecType = RuntimeAsm.GetType("Instella.Installer.Runtime.Builders.CliFlagSpec", throwOnError: true)!;

    private static object NewSpec(string name, Type valueType, object? defaultValue = null, string? mapsTo = null)
    {
        return Activator.CreateInstance(SpecType, name, valueType, defaultValue, (object?)null, mapsTo)!;
    }

    private static CliArgs Parse(string[] args, params object[] specs)
    {
        var list = typeof(System.Collections.Generic.List<>).MakeGenericType(SpecType);
        var listInstance = Activator.CreateInstance(list)!;
        var add = list.GetMethod("Add")!;
        foreach (var s in specs) add.Invoke(listInstance, new[] { s });

        var parse = ParserType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "Parse" && m.GetParameters().Length == 2);
        return (CliArgs)parse.Invoke(null, new object[] { args, listInstance })!;
    }

    [Test]
    public void Parser_parsesStringFlag_spaceSeparated()
    {
        var args = Parse(new[] { "--license-key", "ABC-123" }, NewSpec("--license-key", typeof(string)));
        Assert.That(args.Get<string>("license-key"), Is.EqualTo("ABC-123"));
        Assert.That(args.WasProvided("license-key"), Is.True);
    }

    [Test]
    public void Parser_parsesStringFlag_equalsSeparated()
    {
        var args = Parse(new[] { "--license-key=ABC-123" }, NewSpec("--license-key", typeof(string)));
        Assert.That(args.Get<string>("license-key"), Is.EqualTo("ABC-123"));
    }

    [Test]
    public void Parser_boolFlag_bareIsTrue()
    {
        var args = Parse(new[] { "--accept" }, NewSpec("--accept", typeof(bool), defaultValue: false));
        Assert.That(args.Get<bool>("accept"), Is.True);
    }

    [Test]
    public void Parser_boolFlag_noNegatesToFalse()
    {
        var args = Parse(new[] { "--no-accept" }, NewSpec("--accept", typeof(bool), defaultValue: true));
        Assert.That(args.Get<bool>("accept"), Is.False);
    }

    [Test]
    public void Parser_intFlag_coerces()
    {
        var args = Parse(new[] { "--retries=5" }, NewSpec("--retries", typeof(int), defaultValue: 0));
        Assert.That(args.Get<int>("retries"), Is.EqualTo(5));
    }

    [Test]
    public void Parser_stringArray_commaSeparated()
    {
        var args = Parse(new[] { "--features=a,b,c" }, NewSpec("--features", typeof(string[]), defaultValue: Array.Empty<string>()));
        Assert.That(args.Get<string[]>("features"), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Parser_DirectoryInfo_coerces()
    {
        var args = Parse(new[] { "--dir", "C:\\temp" }, NewSpec("--dir", typeof(DirectoryInfo)));
        Assert.That(args.Get<DirectoryInfo>("dir")!.FullName, Does.Contain("temp"));
    }

    [Test]
    public void Parser_silent_isDetected()
    {
        var args = Parse(new[] { "--silent" });
        Assert.That(args.IsSilent, Is.True);
    }

    [Test]
    public void Parser_defaultValue_usedWhenMissing()
    {
        var args = Parse(System.Array.Empty<string>(), NewSpec("--retries", typeof(int), defaultValue: 3));
        Assert.That(args.Get<int>("retries"), Is.EqualTo(3));
        Assert.That(args.WasProvided("retries"), Is.False);
    }

    [Test]
    public void Parser_unknownFlag_isIgnored()
    {
        var args = Parse(new[] { "--unknown", "value" }, NewSpec("--known", typeof(string)));
        Assert.That(args.WasProvided("unknown"), Is.False);
    }

    [Test]
    public void Build_rejects_unsupportedFlagType()
    {
        var builder = InstellaInstaller.Create()
            .WithApp("Test", "com.test", new Version(1, 0, 0));
        Assert.Throws<InvalidOperationException>(() => builder.AddCliFlag<System.Version>("ver"));
    }

    [Test]
    public void MapCliFlag_storesMappingOnSpec()
    {
        var installer = InstellaInstaller.Create()
            .WithApp("Test", "com.test", new Version(1, 0, 0))
            .AddCliFlag<string>("--license-key", "")
            .MapCliFlag("--license-key", "license.key")
            .AddPage("license", p => p.TextInput("key", "Licence key"))
            .Build();

        // Inspect FrozenConfig.DeclaredCliFlags via reflection.
        var impl = typeof(InstellaInstaller).Assembly.GetType("Instella.Installer.Runtime.Builders.InstellaInstallerImpl")!;
        var config = impl.GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(installer)!;
        var flags = (System.Collections.IList)config.GetType().GetProperty("DeclaredCliFlags")!.GetValue(config)!;
        Assert.That(flags.Count, Is.EqualTo(1));
        var flag = flags[0]!;
        var mapsTo = flag.GetType().GetProperty("MapsTo")!.GetValue(flag);
        Assert.That(mapsTo, Is.EqualTo("license.key"));
    }
}
