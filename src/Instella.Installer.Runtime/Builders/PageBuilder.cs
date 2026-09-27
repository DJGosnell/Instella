using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent builder for a single wizard page. Obtained via
/// <see cref="InstallerBuilder.AddPage"/>; the installer framework holds a
/// reference through the configure delegate, and the user chains widget
/// adders and lifecycle hooks until exiting the delegate. The framework
/// then calls <see cref="Build"/> to freeze the builder into a
/// <see cref="PageSpec"/>.
/// </summary>
public sealed class PageBuilder
{
    private readonly string _id;
    private readonly List<Widget> _widgets = new();
    private Func<PageState, bool>? _continueWhen;
    private Func<InstallContext, CancellationToken, Task>? _onEnter;
    private Func<InstallContext, CancellationToken, Task>? _onLeave;
    private Func<PageState, ValidationResult>? _onValidate;
    private Func<InstallContext, bool>? _when;
    private HashSet<InstallerMode>? _allowedModes;

    internal PageBuilder(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _id = id;
    }

    /// <summary>Adds a heading.</summary>
    public PageBuilder Heading(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        _widgets.Add(new Heading(text));
        return this;
    }

    /// <summary>Adds a paragraph of text.</summary>
    public PageBuilder Paragraph(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        _widgets.Add(new Paragraph(text));
        return this;
    }

    /// <summary>Adds a scrollable block of text, for example a licence.</summary>
    public PageBuilder ScrollableText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _widgets.Add(new ScrollableText(text));
        return this;
    }

    /// <summary>Adds an image scaled to at most <paramref name="maxHeight"/> pixels high.</summary>
    public PageBuilder BrandImage(ImageSource source, int maxHeight = 96)
    {
        ArgumentNullException.ThrowIfNull(source);
        _widgets.Add(new BrandImage(source, maxHeight));
        return this;
    }

    /// <summary>Adds a text box; its value is stored in page state under <paramref name="id"/>.</summary>
    public PageBuilder TextInput(string id, string label, string defaultValue = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        _widgets.Add(new TextInput(label, defaultValue) { Id = id });
        return this;
    }

    /// <summary>Adds a check box; its value is stored in page state under <paramref name="id"/>.</summary>
    public PageBuilder CheckBox(string id, string label, bool defaultValue = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        _widgets.Add(new CheckBox(label, defaultValue) { Id = id });
        return this;
    }

    /// <summary>Adds a group of mutually exclusive options; the chosen value is stored under <paramref name="id"/>.</summary>
    public PageBuilder RadioGroup(string id, string label, params RadioOption[] options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(options);
        _widgets.Add(new RadioGroup(label, options) { Id = id });
        return this;
    }

    /// <summary>Adds a drop-down list; the chosen value is stored under <paramref name="id"/>.</summary>
    public PageBuilder Dropdown(string id, string label, params string[] options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(options);
        _widgets.Add(new Dropdown(label, options) { Id = id });
        return this;
    }

    /// <summary>Adds a folder chooser; the chosen path is stored under <paramref name="id"/>.</summary>
    public PageBuilder FolderPicker(string id, string label, string? defaultPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        _widgets.Add(new FolderPicker(label, defaultPath) { Id = id });
        return this;
    }

    /// <summary>Adds a file chooser; the chosen path is stored under <paramref name="id"/>.</summary>
    public PageBuilder FilePicker(string id, string label, params FileFilter[] filters)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(filters);
        _widgets.Add(new FilePicker(label, filters) { Id = id });
        return this;
    }

    /// <summary>Adds the install progress bar (for pages shown while steps run).</summary>
    public PageBuilder Progress()
    {
        _widgets.Add(new Progress());
        return this;
    }

    /// <summary>Adds the install status text (for pages shown while steps run).</summary>
    public PageBuilder StatusLine()
    {
        _widgets.Add(new StatusLine());
        return this;
    }

    /// <summary>Escape hatch for custom widgets or configured instances (e.g. with Visible/Enabled predicates).</summary>
    public PageBuilder Widget(Widget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        _widgets.Add(widget);
        return this;
    }

    /// <summary>Enables the Continue button only while <paramref name="predicate"/> holds for the page state.</summary>
    public PageBuilder ContinueWhen(Func<PageState, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _continueWhen = predicate;
        return this;
    }

    /// <summary>Runs <paramref name="handler"/> each time the page is shown.</summary>
    public PageBuilder OnEnter(Func<InstallContext, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onEnter = handler;
        return this;
    }

    /// <summary>Runs <paramref name="handler"/> when the user continues past the page.</summary>
    public PageBuilder OnLeave(Func<InstallContext, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onLeave = handler;
        return this;
    }

    /// <summary>Validates the page when the user continues; a failure keeps the user on the page and shows the error.</summary>
    public PageBuilder OnValidate(Func<PageState, ValidationResult> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _onValidate = validator;
        return this;
    }

    /// <summary>
    /// Restrict the page to the given installer modes. When unset, the page
    /// is shown only in <see cref="InstallerMode.FirstInstall"/> (the
    /// conservative default per workflow.md's custom-pages-FirstInstall-only
    /// decision), so a licence page is shown on first installs only. Pass an
    /// explicit set to opt into Upgrade / Repair / Manage.
    /// </summary>
    /// <remarks>
    /// Applies to the wizard and to silent runs alike. The wizard decides the
    /// mode from its default folder before showing the first page, and refuses
    /// an Options-page folder that would change it.
    /// </remarks>
    public PageBuilder InModes(params InstallerMode[] modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        _allowedModes = new HashSet<InstallerMode>(modes);
        return this;
    }

    /// <summary>Shows the page only when <paramref name="predicate"/> holds.</summary>
    public PageBuilder When(Func<InstallContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _when = predicate;
        return this;
    }

    internal PageSpec Build()
    {
        var modes = _allowedModes ?? new HashSet<InstallerMode> { InstallerMode.FirstInstall };
        return new PageSpec(
            Id: _id,
            Widgets: _widgets,
            ContinueWhen: _continueWhen,
            OnEnter: _onEnter,
            OnLeave: _onLeave,
            OnValidate: _onValidate,
            AllowedModes: modes,
            When: _when);
    }
}
