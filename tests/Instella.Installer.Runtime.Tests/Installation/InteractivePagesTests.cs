using System;
using System.Linq;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Exercises <see cref="InteractivePages"/>, the pure page
/// composition helper for the interactive install flow. Tests the happy-
/// path page order + synthetic page shape + user-page filtering + Error /
/// Complete page on-demand construction.
/// </summary>
[TestFixture]
public sealed class InteractivePagesTests
{
    private static FrozenConfig MinimalConfig(Action<InstallerBuilder>? configure = null)
    {
        var b = new InstallerBuilder();
        b.WithApp("QuickNotes", "com.example.quicknotes", new Version(1, 2, 0));
        b.WithPublisher("Example Corp");
        b.WithDescription("A note-taking demonstration app.");
        configure?.Invoke(b);
        var installer = (InstellaInstallerImpl)b.Build();
        return installer.ConfigForTests;
    }

    [Test]
    public void BuildHappyPathPages_minimalConfig_producesWelcomeOptionsProgress()
    {
        var pages = InteractivePages.BuildHappyPathPages(MinimalConfig(), InstallerMode.FirstInstall, "C:\\Install");

        Assert.That(pages.Select(p => p.Id), Is.EqualTo(new[]
        {
            InteractivePageIds.Welcome,
            InteractivePageIds.Options,
            InteractivePageIds.Progress,
        }));
    }

    [Test]
    public void BuildHappyPathPages_withUserPages_insertsBetweenWelcomeAndOptions()
    {
        var config = MinimalConfig(b => b.AddPage("license", p => p
            .Heading("License")
            .ScrollableText("text")
            .CheckBox("accepted", "I accept")
            .ContinueWhen(s => s.Bool("accepted"))));

        var pages = InteractivePages.BuildHappyPathPages(config, InstallerMode.FirstInstall, "C:\\Install");

        Assert.That(pages.Select(p => p.Id), Is.EqualTo(new[]
        {
            InteractivePageIds.Welcome,
            "license",
            InteractivePageIds.Options,
            InteractivePageIds.Progress,
        }));
    }

    [Test]
    public void BuildHappyPathPages_userPageWithWrongMode_isFilteredOut()
    {
        // User page is only allowed in Upgrade; we're running FirstInstall.
        var config = MinimalConfig(b => b.AddPage("upgrade-only", p => p
            .Heading("Upgrade")
            .InModes(InstallerMode.Upgrade)));

        var pages = InteractivePages.BuildHappyPathPages(config, InstallerMode.FirstInstall, "C:\\Install");

        Assert.That(pages.Select(p => p.Id), Does.Not.Contain("upgrade-only"));
    }

    [Test]
    public void BuildHappyPathPages_userPageWithMatchingMode_isIncluded()
    {
        var config = MinimalConfig(b => b.AddPage("upgrade-only", p => p
            .Heading("Upgrade")
            .InModes(InstallerMode.Upgrade)));

        var pages = InteractivePages.BuildHappyPathPages(config, InstallerMode.Upgrade, "C:\\Install");

        Assert.That(pages.Select(p => p.Id), Contains.Item("upgrade-only"));
    }

    [Test]
    public void BuildWelcomePage_includesAppNameVersionPublisher()
    {
        var page = InteractivePages.BuildWelcomePage(MinimalConfig());
        var texts = page.Widgets.OfType<Heading>().Select(h => h.Text).Concat(
            page.Widgets.OfType<Paragraph>().Select(p => p.Text)).ToArray();

        Assert.That(texts.Any(t => t.Contains("QuickNotes")), Is.True);
        Assert.That(texts.Any(t => t.Contains("1.2.0")), Is.True);
        Assert.That(texts.Any(t => t.Contains("Example Corp")), Is.True);
        Assert.That(texts.Any(t => t.Contains("A note-taking demonstration")), Is.True);
    }

    [Test]
    public void BuildOptionsPage_always_hasFolderPickerBoundToInstallPath()
    {
        var page = InteractivePages.BuildOptionsPage(MinimalConfig(), "C:\\Default");
        var folderPicker = page.Widgets.OfType<FolderPicker>().Single();

        Assert.That(folderPicker.Id, Is.EqualTo(InteractivePageStateKeys.InstallPath));
        Assert.That(folderPicker.Default, Is.EqualTo("C:\\Default"));
    }

    [Test]
    public void BuildOptionsPage_noShortcutsConfigured_producesNoCheckboxes()
    {
        var page = InteractivePages.BuildOptionsPage(MinimalConfig(), "C:\\Default");
        Assert.That(page.Widgets.OfType<CheckBox>(), Is.Empty);
    }

    [Test]
    public void BuildProgressPage_withoutWithLaunchAfterInstall_hasNoLaunchCheckbox()
    {
        var page = InteractivePages.BuildProgressPage(MinimalConfig());
        Assert.That(page.Widgets.OfType<CheckBox>(), Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BuildProgressPage_WithLaunchAfterInstall_addsTheLaunchCheckbox(bool checkedByDefault)
    {
        var page = InteractivePages.BuildProgressPage(MinimalConfig(b => b.WithLaunchAfterInstall(checkedByDefault)));

        var launch = page.Widgets.OfType<CheckBox>().Single();
        Assert.That(launch.Id, Is.EqualTo(InteractivePageStateKeys.LaunchApp));
        Assert.That(launch.Label, Is.EqualTo("Launch QuickNotes when I click Finish"));
        Assert.That(launch.Default, Is.EqualTo(checkedByDefault));
    }

    [Test]
    public void BuildOptionsPage_desktopShortcut_addsDesktopCheckbox()
    {
        var page = InteractivePages.BuildOptionsPage(
            MinimalConfig(b => b.WithShortcuts(s => s.Desktop())),
            "C:\\Default");

        var checkboxes = page.Widgets.OfType<CheckBox>().ToArray();
        Assert.That(checkboxes.Select(c => c.Id), Is.EqualTo(new[] { InteractivePageStateKeys.ShortcutDesktop }));
        Assert.That(checkboxes[0].Default, Is.True);
    }

    [Test]
    public void BuildOptionsPage_bothShortcuts_addsBothCheckboxes()
    {
        var page = InteractivePages.BuildOptionsPage(
            MinimalConfig(b => b.WithShortcuts(s => s.Desktop().StartMenu())),
            "C:\\Default");

        var checkboxIds = page.Widgets.OfType<CheckBox>().Select(c => c.Id).ToArray();
        Assert.That(checkboxIds, Is.EquivalentTo(new[]
        {
            InteractivePageStateKeys.ShortcutDesktop,
            InteractivePageStateKeys.ShortcutStartMenu,
        }));
    }

    [Test]
    public void BuildOptionsPage_continueWhen_rejectsEmptyInstallPath()
    {
        var page = InteractivePages.BuildOptionsPage(MinimalConfig(), "C:\\Default");
        var state = new PageState();

        // No install path yet.
        Assert.That(page.ContinueWhen!(state), Is.False);

        state.Set(InteractivePageStateKeys.InstallPath, "C:\\Somewhere");
        Assert.That(page.ContinueWhen!(state), Is.True);
    }

    [Test]
    public void BuildProgressPage_hasProgressAndStatusLine()
    {
        var page = InteractivePages.BuildProgressPage(MinimalConfig());

        Assert.That(page.Widgets.OfType<Progress>().Single().Id, Is.EqualTo(InteractivePageStateKeys.Progress));
        Assert.That(page.Widgets.OfType<StatusLine>().Single().Id, Is.EqualTo(InteractivePageStateKeys.Status));
    }

    [Test]
    public void BuildCompletePage_bakesInstallPathIntoParagraph()
    {
        var page = InteractivePages.BuildCompletePage(MinimalConfig(), "D:\\Apps\\QuickNotes");
        var paragraphTexts = page.Widgets.OfType<Paragraph>().Select(p => p.Text).ToArray();

        Assert.That(paragraphTexts.Any(t => t.Contains("D:\\Apps\\QuickNotes")), Is.True);
    }

    [Test]
    public void BuildErrorPage_withMessage_bakesMessageIntoScrollableText()
    {
        var page = InteractivePages.BuildErrorPage(MinimalConfig(), "Extraction failed: disk full", logFilePath: null);
        var scrollable = page.Widgets.OfType<ScrollableText>().Single();

        Assert.That(scrollable.Text, Does.Contain("Extraction failed"));
    }

    [Test]
    public void BuildErrorPage_withLogPath_addsLogLineParagraph()
    {
        var page = InteractivePages.BuildErrorPage(MinimalConfig(), "failure", "C:\\Temp\\log.log");
        var paragraphs = page.Widgets.OfType<Paragraph>().Select(p => p.Text).ToArray();

        Assert.That(paragraphs.Any(t => t.Contains("C:\\Temp\\log.log")), Is.True);
    }

    [Test]
    public void BuildErrorPage_noLogPath_omitsLogLine()
    {
        var page = InteractivePages.BuildErrorPage(MinimalConfig(), "failure", logFilePath: null);
        var paragraphs = page.Widgets.OfType<Paragraph>().Select(p => p.Text).ToArray();

        Assert.That(paragraphs.Any(t => t.Contains("Log file:")), Is.False);
    }

    [Test]
    public void BuildErrorPage_emptyMessage_fallsBackToGenericText()
    {
        var page = InteractivePages.BuildErrorPage(MinimalConfig(), errorMessage: "", logFilePath: null);
        var scrollable = page.Widgets.OfType<ScrollableText>().Single();

        Assert.That(scrollable.Text, Does.Contain("unable to complete"));
    }

    [Test]
    public void AllSyntheticPages_haveAllowedModesCoveringEveryMode()
    {
        var config = MinimalConfig();
        var pages = InteractivePages.BuildHappyPathPages(config, InstallerMode.FirstInstall, "C:\\Default")
            .Where(p => p.Id.StartsWith("instella-"))
            .Append(InteractivePages.BuildCompletePage(config, "C:\\x"))
            .Append(InteractivePages.BuildErrorPage(config, "x", null));

        foreach (var page in pages)
        {
            foreach (var mode in Enum.GetValues<InstallerMode>())
                Assert.That(page.AllowedModes, Does.Contain(mode), $"page '{page.Id}' missing mode {mode}");
        }
    }

    // ---- readable failure text and step names ----------------------------------

    [Test]
    public void AFailedUserStep_ShowsItsDisplayName_InTheErrorDetails_AndHidesTheLaunchBox()
    {
        var config = MinimalConfig(b => b.WithLaunchAfterInstall().AddStep("drivers", s => s
            .WithDisplayName("Installing drivers")
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Fail("x")))
            .NoRollbackNeeded("test")));
        var page = InteractivePages.BuildProgressPage(config);
        var state = new PageState();
        var result = new Instella.Installer.Runtime.Installation.ExecutionResult(false, "the driver package is missing",
            [new("drivers", InstallStage.Register, Instella.Installer.Runtime.Installation.StepOutcome.Failed, "the driver package is missing")], []);
        var names = Instella.Installer.Runtime.Installation.StepDisplayNames.Map(config.UserSteps);

        InteractiveInstallRunner.ApplyPipelineCompletion(state, result, cancelled: false, _ => { }, _ => { }, names, @"C:\logs\setup.log");

        Assert.That(state.Bool(InteractivePageStateKeys.Failed), Is.True);
        Assert.That(state.Text(InteractivePageStateKeys.Status), Is.EqualTo("Installation failed."));
        Assert.That(state.Text(InteractivePageStateKeys.ErrorDetails), Is.EqualTo(
            "Installing drivers failed: the driver package is missing\n\nChanges were rolled back.\n\nLog: C:\\logs\\setup.log"));
        var details = page.Widgets.OfType<ScrollableText>().Single(w => w.Id == InteractivePageStateKeys.ErrorDetails);
        var launch = page.Widgets.OfType<CheckBox>().Single(w => w.Id == InteractivePageStateKeys.LaunchApp);
        Assert.That(details.Visible!(state), Is.True, "the details show");
        Assert.That(launch.Visible!(state), Is.False, "the launch box hides");
        Assert.That(details.Visible!(new PageState()), Is.False, "no details while installing");
    }

    [Test]
    public void RollbackProblems_AreListed()
    {
        var result = new Instella.Installer.Runtime.Installation.ExecutionResult(false, "disk full",
            [new("extract-payload", InstallStage.Extract, Instella.Installer.Runtime.Installation.StepOutcome.Failed, "disk full")],
            ["could not remove the Desktop shortcut"]);
        var names = Instella.Installer.Runtime.Installation.StepDisplayNames.Map(
            Instella.Installer.Runtime.Installation.OfflineInstallRunner.BuildDefaultSteps());

        var text = InteractiveInstallRunner.ErrorDetails(result, "disk full", names, logFilePath: null);

        Assert.That(text, Is.EqualTo(
            "Copying files failed: disk full\n\nChanges were rolled back, with these problems:\n- could not remove the Desktop shortcut"));
    }

    [TestCase("commit-transaction", null, "Moving files into place…")]
    [TestCase("extract-payload", "verifying files", "Copying files… (verifying files)")]
    [TestCase("my-step", null, "my-step…")]
    public void ProgressStatus_UsesTheDisplayName(string step, string? detail, string expected)
    {
        var names = Instella.Installer.Runtime.Installation.StepDisplayNames.Map(
            Instella.Installer.Runtime.Installation.OfflineInstallRunner.BuildDefaultSteps());

        var text = InteractiveInstallRunner.HostProgressSink.Format(
            new Instella.Installer.Runtime.Installation.OverallProgress(0.5, InstallStage.Finalize, step, detail), names);

        Assert.That(text, Is.EqualTo(expected));
    }
}
