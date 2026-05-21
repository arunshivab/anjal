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

    /// <summary>Maximum number of RCPT TO recipients per transaction. RFC 5321 minimum is 100.</summary>
    public int MaxRecipients { get; init; } = 100;

    /// <summary>Maximum DATA size in bytes. Default 25 MB matches Gmail/Outlook for now.</summary>
    public int MaxMessageBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>Idle-timeout per command. Connections that go this long with no data are closed.</summary>
    public System.TimeSpan CommandTimeout { get; init; } = System.TimeSpan.FromSeconds(60);

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
}
