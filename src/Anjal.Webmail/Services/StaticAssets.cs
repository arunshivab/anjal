using System.Reflection;

namespace Anjal.Webmail.Services;

/// <summary>
/// Serves the design-system assets that are embedded in the assembly:
/// <c>tokens.css</c>, <c>app.css</c>, <c>app.js</c>, the LiPi Sans font
/// files and their stylesheet, and the logo SVGs. Nothing is read from
/// disk and nothing is fetched from a third party, so the webmail works
/// from any working directory and behind a firewall with no outbound
/// access.
/// <para>
/// Fonts and logos are immutable for a year (their content is fixed by
/// the release); the stylesheets and script carry the build's version in
/// their ETag so a redeploy invalidates them.
/// </para>
/// </summary>
public static class StaticAssets
{
    private static readonly Assembly Assembly = typeof(StaticAssets).Assembly;
    private static readonly string ResourcePrefix = Assembly.GetName().Name + ".wwwroot.";
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The build version, used in ETags.</summary>
    public static string Version { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString()
        ?? "dev";

    /// <summary>
    /// Load an embedded asset by its wwwroot-relative path (e.g.
    /// <c>fonts/LiPi-Sans-Tamil.woff2</c>). Returns null when unknown.
    /// </summary>
    /// <param name="relativePath">Path under wwwroot, using forward slashes.</param>
    public static byte[]? Load(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        lock (Gate)
        {
            if (Cache.TryGetValue(relativePath, out byte[]? cached))
            {
                return cached;
            }
        }

        // Embedded resource names replace directory separators with dots;
        // the file's own dots are kept, so "fonts/a.woff2" is "…wwwroot.fonts.a.woff2".
        string resource = ResourcePrefix + relativePath.Replace('/', '.');
        using Stream? stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        lock (Gate)
        {
            Cache[relativePath] = bytes;
        }
        return bytes;
    }

    /// <summary>Content type for an asset path.</summary>
    /// <param name="relativePath">Path under wwwroot.</param>
    public static string ContentType(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    /// <summary>Whether an asset may be cached immutably (content fixed for the life of the URL).</summary>
    /// <param name="relativePath">Path under wwwroot.</param>
    public static bool IsImmutable(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.StartsWith("fonts/", StringComparison.Ordinal) || relativePath.StartsWith("logos/", StringComparison.Ordinal);
    }
}
