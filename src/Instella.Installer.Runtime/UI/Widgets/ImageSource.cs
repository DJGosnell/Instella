using System;
using System.Reflection;

namespace Instella.Installer.Runtime.UI.Widgets;

/// <summary>
/// Abstract reference to an image asset — a file on disk, an embedded
/// resource inside a .NET assembly, or a raw byte payload. Widget renderers
/// accept this type uniformly and pattern-match on the concrete variant to
/// load the image when they render.
/// </summary>
/// <remarks>
/// The three factories cover the realistic authoring needs: <see cref="FromFile"/>
/// for assets next to the installer, <see cref="FromResource"/> for assets
/// embedded in the user's <c>*.Installer</c> assembly (the default —
/// <c>Assembly.GetCallingAssembly</c> avoids the user having to pass their
/// own assembly reference), and <see cref="FromBytes"/> for callers that
/// already have the raw payload in memory. Concrete record types are
/// <see langword="internal"/>; consumers pattern-match via positional
/// deconstruction in the renderer, not via public inspection.
/// </remarks>
public abstract record ImageSource
{
    /// <summary>Source the image from a file on disk.</summary>
    public static ImageSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileImageSource(path);
    }

    /// <summary>
    /// Source the image from an embedded resource. When
    /// <paramref name="asm"/> is <see langword="null"/>, the calling
    /// assembly is used — so <c>ImageSource.FromResource("MyApp.Assets.icon.png")</c>
    /// inside the user's <c>*.Installer</c> project resolves to that project's
    /// assembly without further ceremony.
    /// </summary>
    public static ImageSource FromResource(string name, Assembly? asm = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ResourceImageSource(name, asm ?? Assembly.GetCallingAssembly());
    }

    /// <summary>Source the image from an in-memory byte array.</summary>
    public static ImageSource FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new BytesImageSource(bytes);
    }
}

internal sealed record FileImageSource(string Path) : ImageSource;

internal sealed record ResourceImageSource(string Name, Assembly Assembly) : ImageSource;

internal sealed record BytesImageSource(byte[] Bytes) : ImageSource;
