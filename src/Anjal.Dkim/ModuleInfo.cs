namespace Anjal.Dkim;

/// <summary>
/// RFC 6376 DKIM signing. Implements both <c>simple</c> and <c>relaxed</c>
/// canonicalization for headers and body. Signing is RSA-SHA256 only
/// (Ed25519 deferred until .NET BCL ships <c>System.Security.Cryptography.Ed25519</c>).
/// Zero NuGet dependencies; uses <c>System.Security.Cryptography</c> from the BCL.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Module name as it appears in logs and diagnostics.</summary>
    public const string Name = "Anjal.Dkim";
}
