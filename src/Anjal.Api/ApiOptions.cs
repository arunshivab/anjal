using System.Net;

namespace Anjal.Api;

/// <summary>
/// Configuration for an <see cref="ApiServer"/>.
/// </summary>
public sealed class ApiOptions
{
    /// <summary>
    /// Where to record detail that must not be returned to callers - the
    /// cause of a failed health probe, for instance (DEF-040 observation).
    /// </summary>
    public System.Action<string>? Log { get; init; }

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
    /// A token that is still accepted while it is being replaced. Rotation
    /// without downtime: set this to the old token, put the new one in
    /// <see cref="BearerToken"/>, update the callers (SIGMA, Lipi), then
    /// remove this one and restart (SEC-R3).
    /// </summary>
    public string PreviousBearerToken { get; init; } = string.Empty;

    /// <summary>The shortest admin token accepted: a short one is guessable, and this API can do anything.</summary>
    public const int MinimumTokenLength = 24;

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

    /// <summary>
    /// Maildir root to probe for writability in <c>/healthz</c>. Null skips
    /// the check.
    /// </summary>
    public string? MaildirRoot { get; init; }

    /// <summary>
    /// Days of certificate validity below which <c>/healthz</c> reports the
    /// TLS component as degraded. Default 7.
    /// </summary>
    public int CertificateWarnDays { get; init; } = 7;
}
