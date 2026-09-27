using System.Reflection;

namespace Instella.CLI.Services;

/// <summary>
/// Where the Instella documentation lives: <c>InstellaDocsUrl</c> from Directory.Build.props,
/// compiled in as assembly metadata so a repository move changes one property.
/// </summary>
internal static class InstellaDocs
{
    /// <summary>The docs folder's URL, without a trailing slash.</summary>
    public static string Url { get; } =
        typeof(InstellaDocs).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "InstellaDocsUrl")?.Value?.TrimEnd('/') ?? "docs";

    /// <summary>The URL of one page, for example <c>publishing.md</c>.</summary>
    public static string Page(string page) => $"{Url}/{page}";
}
