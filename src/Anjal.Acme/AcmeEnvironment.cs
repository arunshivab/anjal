using System.Globalization;

namespace Anjal.Acme;

/// <summary>
/// ACME configuration read from environment variables, shared by every
/// Anjal host process so they agree on where the certificate lives.
///
///   ANJAL_ACME_DIR        - certificate directory. Default /var/lib/anjal/acme
///                           (Unix) or %LOCALAPPDATA%\Anjal\acme (Windows).
///   ANJAL_ACME_DOMAINS    - comma-separated DNS names for the certificate.
///                           Empty means ACME is not configured.
///   ANJAL_ACME_EMAIL      - account contact address (expiry notices).
///   ANJAL_ACME_DIRECTORY  - ACME directory URL. Default Let's Encrypt production.
///   ANJAL_ACME_STAGING    - "true" selects Let's Encrypt staging (ignored if
///                           ANJAL_ACME_DIRECTORY is set).
///   ANJAL_ACME_KEY        - "ecdsa-p256" (default) or "rsa-2048".
///   ANJAL_ACME_RENEW_DAYS - renew when fewer days remain. Default 30.
///   ANJAL_ACME_HOST       - "true"/"false": whether THIS process runs the
///                           renewal service. Exactly one process per machine
///                           should. The webmail defaults to true when domains
///                           are configured; the server defaults to false.
///   ANJAL_ACME_HTTP_BIND  - address for the HTTP-01 responder when the server
///                           hosts renewal. Default "+" (all interfaces).
///   ANJAL_ACME_HTTP_PORT  - port for that responder. Default 80.
/// </summary>
public sealed class AcmeEnvironment
{
    /// <summary>Certificate directory.</summary>
    public string Directory { get; init; } = DefaultDirectory;

    /// <summary>Configured domains; empty when ACME is off.</summary>
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    /// <summary>Whether domains were configured.</summary>
    public bool Configured => this.Domains.Count > 0;

    /// <summary>Whether this process should run the renewal service.</summary>
    public bool Host { get; init; }

    /// <summary>HTTP-01 responder bind address (server-hosted mode).</summary>
    public string HttpBind { get; init; } = "+";

    /// <summary>HTTP-01 responder port (server-hosted mode).</summary>
    public int HttpPort { get; init; } = 80;

    /// <summary>The renewal options built from the environment.</summary>
    public AcmeOptions Options { get; init; } = new();

    /// <summary>Platform default certificate directory.</summary>
    public static string DefaultDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anjal", "acme")
            : "/var/lib/anjal/acme";

    /// <summary>
    /// Read the environment.
    /// </summary>
    /// <param name="hostByDefault">What ANJAL_ACME_HOST means when unset: the webmail passes true, the server false.</param>
    public static AcmeEnvironment Read(bool hostByDefault)
    {
        string? dir = Environment.GetEnvironmentVariable("ANJAL_ACME_DIR");
        string domainsRaw = Environment.GetEnvironmentVariable("ANJAL_ACME_DOMAINS") ?? string.Empty;
        var domains = new List<string>();
        foreach (string d in domainsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            domains.Add(d.ToLowerInvariant());
        }

        string? directoryUrl = Environment.GetEnvironmentVariable("ANJAL_ACME_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directoryUrl))
        {
            bool staging = string.Equals(Environment.GetEnvironmentVariable("ANJAL_ACME_STAGING"), "true", StringComparison.OrdinalIgnoreCase);
            directoryUrl = staging ? AcmeDirectories.LetsEncryptStaging : AcmeDirectories.LetsEncrypt;
        }

        string keyRaw = (Environment.GetEnvironmentVariable("ANJAL_ACME_KEY") ?? "ecdsa-p256").Trim().ToLowerInvariant();
        CertificateKeyType keyType = keyRaw == "rsa-2048" || keyRaw == "rsa" ? CertificateKeyType.Rsa2048 : CertificateKeyType.EcdsaP256;

        int renewDays = 30;
        string? renewRaw = Environment.GetEnvironmentVariable("ANJAL_ACME_RENEW_DAYS");
        if (renewRaw is not null && int.TryParse(renewRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rd) && rd > 0)
        {
            renewDays = rd;
        }

        string? hostRaw = Environment.GetEnvironmentVariable("ANJAL_ACME_HOST");
        bool host = hostRaw is null ? hostByDefault : string.Equals(hostRaw, "true", StringComparison.OrdinalIgnoreCase);

        int httpPort = 80;
        string? portRaw = Environment.GetEnvironmentVariable("ANJAL_ACME_HTTP_PORT");
        if (portRaw is not null && int.TryParse(portRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hp) && hp > 0)
        {
            httpPort = hp;
        }

        return new AcmeEnvironment
        {
            Directory = string.IsNullOrWhiteSpace(dir) ? DefaultDirectory : dir,
            Domains = domains,
            Host = host && domains.Count > 0,
            HttpBind = Environment.GetEnvironmentVariable("ANJAL_ACME_HTTP_BIND") ?? "+",
            HttpPort = httpPort,
            Options = new AcmeOptions
            {
                DirectoryUrl = directoryUrl,
                Domains = domains,
                ContactEmail = Environment.GetEnvironmentVariable("ANJAL_ACME_EMAIL") ?? string.Empty,
                KeyType = keyType,
                RenewBefore = TimeSpan.FromDays(renewDays),
            },
        };
    }
}
