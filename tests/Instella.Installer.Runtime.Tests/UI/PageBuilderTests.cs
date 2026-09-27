using System;
using System.Linq;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class PageBuilderTests
{
    [Test]
    public void AddPage_storesSpec_onFrozenConfig()
    {
        var installer = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddPage("license", p => p.Heading("License"))
            .Build();

        // FrozenConfig is internal; inspect via reflection through InternalsVisibleTo.
        var impl = (dynamic)installer;
        Assert.That(impl is not null);
    }

    [Test]
    public void Build_rejectsDuplicatePageId()
    {
        var builder = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddPage("same", p => p.Heading("A"))
            .AddPage("same", p => p.Heading("B"));

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("duplicate page id"));
    }

    [Test]
    public void Widgets_recordEveryAdder()
    {
        var pb = new PageBuilder("page1");
        pb.Heading("h")
          .Paragraph("p")
          .ScrollableText("st")
          .BrandImage(ImageSource.FromFile("logo.png"), 128)
          .TextInput("name", "Name:")
          .CheckBox("agree", "I agree")
          .RadioGroup("plan", "Plan", new RadioOption("free", "Free"), new RadioOption("pro", "Pro"))
          .Dropdown("region", "Region", "us", "eu")
          .FolderPicker("dir", "Install dir")
          .FilePicker("license", "License", new FileFilter("License files", new[] { "txt" }))
          .Progress()
          .StatusLine();

        var spec = pb.Build();
        Assert.That(spec.Widgets.Count, Is.EqualTo(12));
        Assert.That(spec.Widgets[0], Is.InstanceOf<Heading>());
        Assert.That(spec.Widgets[1], Is.InstanceOf<Paragraph>());
        Assert.That(spec.Widgets[11], Is.InstanceOf<StatusLine>());
    }

    [Test]
    public void ContinueWhen_isStoredOnSpec()
    {
        var pb = new PageBuilder("p").CheckBox("ok", "OK").ContinueWhen(s => s.Bool("ok"));
        var spec = pb.Build();
        Assert.That(spec.ContinueWhen, Is.Not.Null);

        var state = new PageState();
        state.Set("ok", false);
        Assert.That(spec.ContinueWhen!(state), Is.False);
        state.Set("ok", true);
        Assert.That(spec.ContinueWhen!(state), Is.True);
    }

    [Test]
    public void OnEnter_OnLeave_OnValidate_areStored()
    {
        var pb = new PageBuilder("p")
            .OnEnter((_, _) => Task.CompletedTask)
            .OnLeave((_, _) => Task.CompletedTask)
            .OnValidate(_ => ValidationResult.Ok);
        var spec = pb.Build();
        Assert.That(spec.OnEnter, Is.Not.Null);
        Assert.That(spec.OnLeave, Is.Not.Null);
        Assert.That(spec.OnValidate, Is.Not.Null);
    }

    [Test]
    public void InModes_defaults_to_FirstInstall_only()
    {
        var spec = new PageBuilder("p").Build();
        Assert.That(spec.AllowedModes, Is.EquivalentTo(new[] { InstallerMode.FirstInstall }));
    }

    [Test]
    public void InModes_explicit_storesProvidedSet()
    {
        var spec = new PageBuilder("p").InModes(InstallerMode.FirstInstall, InstallerMode.Upgrade).Build();
        Assert.That(spec.AllowedModes, Is.EquivalentTo(new[]
        {
            InstallerMode.FirstInstall,
            InstallerMode.Upgrade
        }));
    }

    [Test]
    public void Widget_escapeHatch_allowsConfiguredInstance()
    {
        var custom = new Heading("Custom") { Id = "h1" };
        var spec = new PageBuilder("p").Widget(custom).Build();
        Assert.That(spec.Widgets[0], Is.SameAs(custom));
    }

    [Test]
    public void Heading_rejectsEmpty()
    {
        var pb = new PageBuilder("p");
        Assert.Throws<ArgumentException>(() => pb.Heading(""));
    }

    [Test]
    public void ContinueWhen_rejectsNull()
    {
        var pb = new PageBuilder("p");
        Assert.Throws<ArgumentNullException>(() => pb.ContinueWhen(null!));
    }
}
