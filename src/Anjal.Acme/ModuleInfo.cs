namespace Anjal.Acme;

/// <summary>
/// Metadata for the Anjal.Acme module: an RFC 8555 (ACME v2) client with
/// HTTP-01 challenges, PEM certificate storage and a renewal scheduler,
/// so an Anjal deployment obtains and renews its own TLS certificate
/// without external tooling.
/// </summary>
public static class ModuleInfo
{
    /// <summary>The semantic version of this module.</summary>
    public static string Version => "0.1.0";

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Acme";
}
