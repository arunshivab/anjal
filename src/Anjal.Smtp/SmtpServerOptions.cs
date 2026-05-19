using System.Net;

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
}
