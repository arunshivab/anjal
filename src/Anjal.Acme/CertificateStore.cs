using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Anjal.Acme;

/// <summary>
/// Holds HTTP-01 key authorizations while a challenge is pending. The
/// process that owns port 80 serves
/// <c>/.well-known/acme-challenge/{token}</c> from this table. It is a
/// process-wide singleton so any HTTP host (Kestrel or HttpListener) can
/// answer without knowing which renewal is in flight.
/// </summary>
public static class Http01ChallengeStore
{
    /// <summary>The URL path prefix ACME validators request.</summary>
    public const string PathPrefix = "/.well-known/acme-challenge/";

    private static readonly ConcurrentDictionary<string, string> Tokens = new(StringComparer.Ordinal);

    /// <summary>Register a token with its key authorization.</summary>
    /// <param name="token">Challenge token.</param>
    /// <param name="keyAuthorization">Key authorization to serve.</param>
    public static void Add(string token, string keyAuthorization)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(keyAuthorization);
        Tokens[token] = keyAuthorization;
    }

    /// <summary>Remove a token after validation.</summary>
    /// <param name="token">Challenge token.</param>
    public static void Remove(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        Tokens.TryRemove(token, out _);
    }

    /// <summary>
    /// Resolve a request path to a key authorization. Returns null when
    /// the path is not a challenge path or the token is unknown.
    /// </summary>
    /// <param name="requestPath">Absolute request path.</param>
    public static string? Lookup(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        if (!requestPath.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        string token = requestPath.Substring(PathPrefix.Length);
        if (token.Length == 0 || token.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }
        return Tokens.TryGetValue(token, out string? ka) ? ka : null;
    }

    /// <summary>Number of pending tokens (for status and tests).</summary>
    public static int Count => Tokens.Count;
}

/// <summary>Certificate key algorithm.</summary>
public enum CertificateKeyType
{
    /// <summary>ECDSA P-256 (default): small, fast, universally accepted by modern clients.</summary>
    EcdsaP256 = 0,

    /// <summary>RSA-2048: maximum compatibility with very old clients.</summary>
    Rsa2048 = 1,
}

/// <summary>Metadata saved next to the certificate files.</summary>
public sealed class CertificateMetadata
{
    /// <summary>ACME directory the certificate came from.</summary>
    public string DirectoryUrl { get; set; } = string.Empty;

    /// <summary>Domains in the certificate.</summary>
    public string[] Domains { get; set; } = Array.Empty<string>();

    /// <summary>Key type used.</summary>
    public string KeyType { get; set; } = string.Empty;

    /// <summary>When issued (UTC).</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>NotAfter of the leaf (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// PEM files in one directory: <c>account.key.pem</c>, <c>cert.key.pem</c>,
/// <c>fullchain.pem</c>, <c>meta.json</c>. Certificates are written to a
/// temp file and renamed so a reader never sees a partial file. Any
/// process that can read the directory can load the certificate; only
/// the process hosting renewal writes it.
/// </summary>
public sealed class CertificateStore
{
    /// <summary>Construct.</summary>
    /// <param name="directory">Directory path (created on first write).</param>
    public CertificateStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory must not be empty.", nameof(directory));
        }
        this.Directory = Path.GetFullPath(directory);
    }

    /// <summary>The directory.</summary>
    public string Directory { get; }

    /// <summary>Path of the account key PEM.</summary>
    public string AccountKeyPath => Path.Combine(this.Directory, "account.key.pem");

    /// <summary>Path of the certificate private key PEM.</summary>
    public string CertificateKeyPath => Path.Combine(this.Directory, "cert.key.pem");

    /// <summary>Path of the full chain PEM (leaf first).</summary>
    public string FullChainPath => Path.Combine(this.Directory, "fullchain.pem");

    /// <summary>Path of the metadata JSON.</summary>
    public string MetadataPath => Path.Combine(this.Directory, "meta.json");

    /// <summary>Path of the renew-now request marker (see <see cref="RequestRenewal"/>).</summary>
    public string RenewRequestPath => Path.Combine(this.Directory, "renew.request");

    /// <summary>Path of the status JSON written by the renewal service.</summary>
    public string StatusPath => Path.Combine(this.Directory, "status.json");

    /// <summary>Load the account key, creating and saving one if absent.</summary>
    public AccountKey LoadOrCreateAccountKey()
    {
        if (File.Exists(this.AccountKeyPath))
        {
            return AccountKey.FromPem(File.ReadAllText(this.AccountKeyPath));
        }
        System.IO.Directory.CreateDirectory(this.Directory);
        AccountKey key = AccountKey.Create();
        WriteAtomic(this.AccountKeyPath, key.ToPem(), restrict: true);
        return key;
    }

    /// <summary>Generate a fresh certificate key of the given type (not saved until <see cref="Save"/>).</summary>
    /// <param name="type">Key type.</param>
    public static AsymmetricAlgorithm CreateCertificateKey(CertificateKeyType type) => type switch
    {
        CertificateKeyType.Rsa2048 => RSA.Create(2048),
        _ => ECDsa.Create(ECCurve.NamedCurves.nistP256),
    };

    /// <summary>Save a newly issued certificate and its key atomically.</summary>
    /// <param name="certificateKey">The private key the CSR was built with.</param>
    /// <param name="fullChainPem">Chain PEM from the CA, leaf first.</param>
    /// <param name="metadata">Metadata to record.</param>
    public void Save(AsymmetricAlgorithm certificateKey, string fullChainPem, CertificateMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(certificateKey);
        ArgumentNullException.ThrowIfNull(fullChainPem);
        ArgumentNullException.ThrowIfNull(metadata);
        System.IO.Directory.CreateDirectory(this.Directory);
        string keyPem = certificateKey switch
        {
            ECDsa ec => ec.ExportPkcs8PrivateKeyPem(),
            RSA rsa => rsa.ExportPkcs8PrivateKeyPem(),
            _ => throw new ArgumentException("Key must be ECDsa or RSA.", nameof(certificateKey)),
        };
        // Key first, then chain: a reader that sees the new chain will find a matching key.
        WriteAtomic(this.CertificateKeyPath, keyPem, restrict: true);
        WriteAtomic(this.FullChainPath, fullChainPem, restrict: false);
        WriteAtomic(this.MetadataPath, JsonSerializer.Serialize(metadata, JsonOptions), restrict: false);
    }

    /// <summary>Whether a certificate has been saved.</summary>
    public bool HasCertificate => File.Exists(this.FullChainPath) && File.Exists(this.CertificateKeyPath);

    /// <summary>Last write time of the chain file (UTC), or null if none.</summary>
    public DateTime? CertificateWrittenAtUtc => File.Exists(this.FullChainPath) ? File.GetLastWriteTimeUtc(this.FullChainPath) : null;

    /// <summary>
    /// Load the current certificate with its private key. Returns null if
    /// no certificate is stored or the files cannot be parsed. The
    /// returned certificate is exportable and usable by Kestrel and
    /// SslStream on all platforms.
    /// </summary>
    public X509Certificate2? LoadCertificate()
    {
        if (!this.HasCertificate)
        {
            return null;
        }
        try
        {
            using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(this.FullChainPath, this.CertificateKeyPath);
            // Re-import through PKCS#12 so the private key is attached in a
            // way every TLS stack accepts (ephemeral PEM keys are not on Windows).
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Load the intermediate certificates from the chain (everything after the leaf).</summary>
    public X509Certificate2Collection LoadChain()
    {
        var chain = new X509Certificate2Collection();
        if (!File.Exists(this.FullChainPath))
        {
            return chain;
        }
        chain.ImportFromPemFile(this.FullChainPath);
        if (chain.Count > 0)
        {
            chain.RemoveAt(0);
        }
        return chain;
    }

    /// <summary>Load metadata, or null.</summary>
    public CertificateMetadata? LoadMetadata()
    {
        if (!File.Exists(this.MetadataPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CertificateMetadata>(File.ReadAllText(this.MetadataPath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ask the renewal service (which may be another process) to renew at
    /// its next check regardless of expiry, by dropping a marker file.
    /// </summary>
    public void RequestRenewal()
    {
        System.IO.Directory.CreateDirectory(this.Directory);
        File.WriteAllText(this.RenewRequestPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Consume the renew-now marker. Returns true if one was present.</summary>
    public bool TakeRenewalRequest()
    {
        if (!File.Exists(this.RenewRequestPath))
        {
            return false;
        }
        try
        {
            File.Delete(this.RenewRequestPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Write the renewal service status for other processes to read.</summary>
    /// <param name="status">Status.</param>
    public void WriteStatus(AcmeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        System.IO.Directory.CreateDirectory(this.Directory);
        WriteAtomic(this.StatusPath, JsonSerializer.Serialize(status, JsonOptions), restrict: false);
    }

    /// <summary>Read the last written status, or null.</summary>
    public AcmeStatus? ReadStatus()
    {
        if (!File.Exists(this.StatusPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AcmeStatus>(File.ReadAllText(this.StatusPath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void WriteAtomic(string path, string content, bool restrict)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (restrict && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Watches a <see cref="CertificateStore"/> and hands out the current
/// certificate, reloading when the chain file changes. Both the SMTP
/// server and the webmail use one of these, so a renewal by either
/// process is picked up by the other within <see cref="PollInterval"/>
/// with no restart.
/// </summary>
public sealed class CertificateWatcher : IDisposable
{
    private readonly CertificateStore store;
    private readonly object gate = new();
    private X509Certificate2? current;
    private DateTime? loadedWriteTime;
    private DateTimeOffset lastCheck = DateTimeOffset.MinValue;

    /// <summary>Construct.</summary>
    /// <param name="store">Store to watch.</param>
    public CertificateWatcher(CertificateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>Minimum time between file-system checks. Default 30 seconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised after a new certificate has been loaded.</summary>
    public event Action<X509Certificate2>? Reloaded;

    /// <summary>
    /// The current certificate, or null if none is stored. Cheap to call
    /// per connection: the file is only stat'ed once per poll interval.
    /// </summary>
    public X509Certificate2? Current
    {
        get
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (this.gate)
            {
                if (now - this.lastCheck < this.PollInterval && this.current is not null)
                {
                    return this.current;
                }
                this.lastCheck = now;
                DateTime? written = this.store.CertificateWrittenAtUtc;
                if (written is null)
                {
                    return this.current;
                }
                if (this.current is not null && written == this.loadedWriteTime)
                {
                    return this.current;
                }
                X509Certificate2? loaded = this.store.LoadCertificate();
                if (loaded is null)
                {
                    return this.current;
                }
                X509Certificate2? old = this.current;
                this.current = loaded;
                this.loadedWriteTime = written;
                old?.Dispose();
                this.Reloaded?.Invoke(loaded);
                return this.current;
            }
        }
    }

    /// <summary>Force the next <see cref="Current"/> call to re-check the files.</summary>
    public void Invalidate()
    {
        lock (this.gate)
        {
            this.lastCheck = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (this.gate)
        {
            this.current?.Dispose();
            this.current = null;
        }
    }
}
