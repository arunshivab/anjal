namespace Anjal.Api;

/// <summary>
/// Metadata for the Anjal.Api module. The module exposes the HTTP/JSON
/// surface that applications use to send mail and manage routing rules.
/// </summary>
public static class ModuleInfo
{
    /// <summary>The semantic version of this module.</summary>
    public static string Version => "0.1.0";

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Api";
}
