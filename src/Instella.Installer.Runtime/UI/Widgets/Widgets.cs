using System.Collections.Generic;

namespace Instella.Installer.Runtime.UI.Widgets;

/// <summary>Static text rendered as a heading / page title.</summary>
public sealed record Heading(string Text) : Widget;

/// <summary>Wrapping static text, typically a single paragraph of body copy.</summary>
public sealed record Paragraph(string Text) : Widget;

/// <summary>Long, scrollable read-only text — license agreements, release notes.</summary>
public sealed record ScrollableText(string Text) : Widget;

/// <summary>An image asset (logo, brand artwork) with a max-height constraint.</summary>
public sealed record BrandImage(ImageSource Source, int MaxHeight = 96) : Widget;

/// <summary>Single-line text input bound to <see cref="Widget.Id"/>.</summary>
public sealed record TextInput(string Label, string Default = "") : Widget;

/// <summary>Boolean check box bound to <see cref="Widget.Id"/>.</summary>
public sealed record CheckBox(string Label, bool Default = false) : Widget;

/// <summary>Mutually-exclusive option group bound to <see cref="Widget.Id"/>.</summary>
public sealed record RadioGroup(string Label, IReadOnlyList<RadioOption> Options, string? Default = null) : Widget;

/// <summary>Drop-down list bound to <see cref="Widget.Id"/>.</summary>
public sealed record Dropdown(string Label, IReadOnlyList<string> Options, string? Default = null) : Widget;

/// <summary>Folder chooser bound to <see cref="Widget.Id"/>.</summary>
public sealed record FolderPicker(string Label, string? Default = null) : Widget;

/// <summary>File chooser with an optional filter list bound to <see cref="Widget.Id"/>.</summary>
public sealed record FilePicker(string Label, IReadOnlyList<FileFilter> Filters, string? Default = null) : Widget;

/// <summary>Determinate progress bar. Driven by the current install step's <c>IStepProgress</c>.</summary>
public sealed record Progress : Widget;

/// <summary>Single-line status message driven by the current install step's progress reports.</summary>
public sealed record StatusLine : Widget;

/// <summary>One option inside a <see cref="RadioGroup"/>.</summary>
/// <param name="Value">Stored value (written to <see cref="PageState"/>).</param>
/// <param name="Label">Display label.</param>
public sealed record RadioOption(string Value, string Label);

/// <summary>One filter in a <see cref="FilePicker"/>.</summary>
/// <param name="Description">Human-readable description (e.g. "Images").</param>
/// <param name="Extensions">List of extensions (no dot), e.g. <c>["png", "jpg"]</c>.</param>
public sealed record FileFilter(string Description, string[] Extensions);
