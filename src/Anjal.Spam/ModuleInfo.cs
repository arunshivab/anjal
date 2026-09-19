namespace Anjal.Spam;

/// <summary>
/// Metadata for the Anjal.Spam module. Minimum-heuristics anti-spam:
/// authentication-result scoring, sender/HELO/DNS sanity checks, simple
/// content rules, per-tenant allow/block lists, connection rate limits
/// and greylisting. Verdicts are recorded in headers and used to file
/// mail in Junk; nothing here rejects mail unless explicitly configured.
/// </summary>
public static class ModuleInfo
{
    /// <summary>The semantic version of this module.</summary>
    public static string Version => "0.1.0";

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Spam";
}
