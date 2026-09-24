using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Anjal.Smtp;

/// <summary>
/// Configuration for an <see cref="SmtpServer"/> instance.
/// </summary>
public sealed class SmtpServerOptions
{
    /// <summary>IP address to bind. Defaults to loopback for local development.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>TCP port to listen on. Default 2525 to avoid needing admin on dev machines.</summary>
    public int Port { get; init; } = 2525;

    /// <summary>The hostname this server announces in 220 banners and EHLO responses.</summary>
    public string AdvertisedHostName { get; init; } = "anjal.localhost";

    /// <summary>
    /// Optional check for whether a recipient on one of our own domains
    /// exists, so an unknown one is refused at RCPT TO instead of after the
    /// message is transferred (DEF-042). Null leaves the decision to delivery.
    /// </summary>
    public IRecipientResolver? Recipients { get; init; }

    /// <summary>Maximum number of RCPT TO recipients per transaction. RFC 5321 minimum is 100.</summary>
    public int MaxRecipients { get; init; } = 100;

    /// <summary>Maximum DATA size in bytes. Default 25 MB matches Gmail/Outlook for now.</summary>
    public int MaxMessageBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>Idle-timeout per command. Connections that go this long with no data are closed.</summary>
    public System.TimeSpan CommandTimeout { get; init; } = System.TimeSpan.FromSeconds(120);

    /// <summary>
    /// The longest a single session may last, however active. Stops a client
    /// that drips one byte just inside <see cref="CommandTimeout"/> from
    /// holding a connection forever. Generous enough for a 25 MB message
    /// over a slow link. Default 15 minutes.
    /// </summary>
    public System.TimeSpan MaxSessionDuration { get; init; } = System.TimeSpan.FromMinutes(15);

    /// <summary>
    /// Concurrent sessions this listener serves at once. Past this, new
    /// connections get <c>421 4.7.0</c> and are closed before a session is
    /// created. Default 200.
    /// </summary>
    public int MaxConcurrentSessions { get; init; } = 200;

    /// <summary>
    /// Concurrent sessions from one client address. Large senders open a few
    /// connections in parallel; ten leaves room for that. Default 10.
    /// </summary>
    public int MaxSessionsPerAddress { get; init; } = 10;

    /// <summary>Failed AUTH attempts allowed in one session before it is closed. Default 3.</summary>
    public int MaxAuthFailuresPerSession { get; init; } = 3;

    /// <summary>
    /// Shared per-address AUTH failure counter for this listener. Null
    /// disables the cross-session limit (the per-session one still applies).
    /// </summary>
    public AuthFailureLimiter? AuthFailures { get; init; }

    /// <summary>
    /// Implicit TLS (RFC 8314): the TLS handshake happens as soon as the
    /// client connects, before the banner, as on port 465. No plaintext is
    /// ever exchanged, so there is no STARTTLS to strip. A connection whose
    /// handshake fails is closed without a word.
    /// </summary>
    public bool ImplicitTls { get; init; }

    /// <summary>Optional diagnostic log for errors a session recovers from but should not hide.</summary>
    public System.Action<string>? Log { get; init; }

    /// <summary>
    /// X.509 certificate (with private key) used for STARTTLS. When set,
    /// the server advertises <c>STARTTLS</c> in EHLO and accepts upgrades.
    /// When null, STARTTLS is not advertised and the server runs plaintext only.
    /// </summary>
    public X509Certificate2? TlsCertificate { get; init; }

    /// <summary>
    /// When true, the server refuses <c>MAIL FROM</c>, <c>RCPT TO</c>, and
    /// <c>DATA</c> on plaintext connections after EHLO - clients must
    /// <c>STARTTLS</c> first. Has no effect if <see cref="TlsCertificate"/>
    /// is null. Defaults to false (TLS opportunistic on receiver side).
    /// </summary>
    public bool RequireTlsForMail { get; init; }

    /// <summary>
    /// The role this listener plays. Determines which commands are
    /// accepted and which authorization checks apply. Defaults to
    /// <see cref="SmtpServerRole.Mta"/> which is correct for a public
    /// port 25 listener.
    /// </summary>
    public SmtpServerRole Role { get; init; } = SmtpServerRole.Mta;

    /// <summary>
    /// For <see cref="SmtpServerRole.Submission"/>, allow <c>AUTH</c>
    /// commands on plaintext connections (no STARTTLS). DEFAULT IS FALSE.
    /// Enable ONLY for local development or controlled networks - AUTH
    /// without TLS leaks credentials. The submission port refuses
    /// authentication unless this is true or TLS is active.
    /// </summary>
    public bool AllowPlaintextAuth { get; init; }

    /// <summary>
    /// Optional connection/transaction policy (rate limiting, greylisting).
    /// Null means every connection and command is allowed.
    /// </summary>
    public ISmtpPolicy? Policy { get; init; }

    /// <summary>
    /// Optional live certificate source, consulted at the start of every
    /// session. When set it takes precedence over
    /// <see cref="TlsCertificate"/>, so a renewed certificate is used by
    /// new connections without restarting the server. Returning null
    /// means "no certificate yet" and disables STARTTLS for that session.
    /// </summary>
    public System.Func<X509Certificate2?>? TlsCertificateSource { get; init; }

    /// <summary>
    /// The certificate to use for a session starting now: the
    /// <see cref="TlsCertificateSource"/> result if a source is set,
    /// otherwise <see cref="TlsCertificate"/>.
    /// </summary>
    public X509Certificate2? CurrentTlsCertificate() => this.TlsCertificateSource is null ? this.TlsCertificate : this.TlsCertificateSource();
}
