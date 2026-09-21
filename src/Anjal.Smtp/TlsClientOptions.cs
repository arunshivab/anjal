namespace Anjal.Smtp;

/// <summary>
/// TLS configuration for an outbound sender. Determines whether to attempt
/// STARTTLS and how strict to be when the remote server lacks it.
/// </summary>
public sealed class TlsClientOptions
{
    /// <summary>
    /// The TLS mode. The default is opportunistic which is what every
    /// legitimate MTA does: try STARTTLS, fall back to plaintext if the
    /// server does not advertise it. Override per-destination via the
    /// store's <c>outbound_tls_policies</c> table.
    /// </summary>
    public Anjal.Store.TlsMode DefaultMode { get; init; } = Anjal.Store.TlsMode.Opportunistic;

    /// <summary>
    /// When true (default), the server's certificate is validated against
    /// the system trust store. Set false ONLY for testing against
    /// self-signed certificates.
    /// </summary>
    public bool ValidateCertificate { get; init; } = true;

    /// <summary>
    /// Revocation checking when a certificate is validated (Required mode and
    /// relays). Default <see cref="System.Security.Cryptography.X509Certificates.X509RevocationMode.Online"/>.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509RevocationMode Revocation { get; init; } =
        System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;

    /// <summary>
    /// A function that returns the TLS mode for a given destination
    /// (domain for direct, hostname for relay). May return null to use
    /// <see cref="DefaultMode"/>. Typically backed by the store.
    /// </summary>
    public System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<Anjal.Store.TlsMode?>>? PolicyLookup { get; init; }

    /// <summary>
    /// Resolve the effective TLS mode for a destination, applying the
    /// policy lookup if available and falling back to the default.
    /// </summary>
    /// <param name="destination">The destination domain or hostname.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<Anjal.Store.TlsMode> ResolveModeAsync(
        string destination,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(destination);
        if (this.PolicyLookup is null)
        {
            return this.DefaultMode;
        }
        Anjal.Store.TlsMode? specific = await this.PolicyLookup(destination, ct).ConfigureAwait(false);
        return specific ?? this.DefaultMode;
    }
}
