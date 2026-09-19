namespace Anjal.Mailbox;

/// <summary>
/// Metadata for the Anjal.Mailbox module. The module stores delivered
/// mail in per-tenant, per-user Maildir directories and indexes it in the
/// store for listing and retrieval.
/// </summary>
public static class ModuleInfo
{
    /// <summary>The semantic version of this module.</summary>
    public static string Version => "0.1.0";

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Mailbox";
}
