namespace Anjal.Routing;

/// <summary>
/// Metadata for the Anjal.Routing module. The module maintains the address
/// routing table that maps inbound mailboxes to application webhooks.
/// </summary>
public static class ModuleInfo
{
    /// <summary>The semantic version of this module.</summary>
    public static string Version => "0.1.0";

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Routing";
}
