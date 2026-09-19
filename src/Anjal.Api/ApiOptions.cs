using System.Net;

namespace Anjal.Api;

/// <summary>
/// Configuration for an <see cref="ApiServer"/>.
/// </summary>
public sealed class ApiOptions
{
    /// <summary>The IP address to bind. Loopback by default for local dev.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>TCP port to listen on. 8080 is a common dev choice.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>
    /// Shared bearer token. Clients must send <c>Authorization: Bearer &lt;value&gt;</c>.
    /// Empty token disables auth - useful for local tests, never for production.
    /// </summary>
    public string BearerToken { get; init; } = string.Empty;

    /// <summary>
    /// Maximum request body size in bytes. Requests larger than this receive 413.
    /// Default 25 MB - matches the SMTP server's MaxMessageBytes.
    /// </summary>
    public int MaxBodyBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>
    /// ACME certificate directory (see <c>Anjal.Acme.CertificateStore</c>).
    /// When set, <c>GET /api/acme</c> reports certificate and renewal status
    /// and <c>POST /api/acme/renew</c> requests an immediate renewal from
    /// whichever process hosts the renewal service. Null disables both routes.
    /// </summary>
    public string? AcmeDirectory { get; init; }
}
