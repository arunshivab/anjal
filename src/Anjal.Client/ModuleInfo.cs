namespace Anjal.Client;

/// <summary>
/// rc.26 (DES-14 §28.6): the package for .NET applications that use Anjal's application API -
/// <see cref="AnjalClient"/> (send, batch, status, events, inbox, acknowledgement, suppressions),
/// request signing with an <see cref="System.Security.Cryptography.ECDsa"/> key,
/// <see cref="InboxMessage.Decrypt(System.Security.Cryptography.RSA)"/> and <see cref="WebhookVerifier"/>.
/// Only System.Text.Json and HttpClient.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Module name as it appears in logs and diagnostics.</summary>
    public const string Name = "Anjal.Client";

    /// <summary>The API version this package speaks.</summary>
    public const string ApiVersion = "v1";
}
