using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Anjal.Acme;

/// <summary>Configuration for <see cref="AcmeRenewalService"/>.</summary>
public sealed class AcmeOptions
{
    /// <summary>ACME directory URL. Default Let's Encrypt production.</summary>
    public string DirectoryUrl { get; init; } = AcmeDirectories.LetsEncrypt;

    /// <summary>DNS names for the certificate; the first is the subject CN.</summary>
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    /// <summary>Contact email for the account (expiry notices), or empty.</summary>
    public string ContactEmail { get; init; } = string.Empty;

    /// <summary>Certificate key type.</summary>
    public CertificateKeyType KeyType { get; init; } = CertificateKeyType.EcdsaP256;

    /// <summary>Renew when less than this remains before expiry. Default 30 days.</summary>
    public TimeSpan RenewBefore { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the service checks expiry and the renew-now marker. Default 1 hour.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long to poll a challenge or order before giving up. Default 2 minutes.</summary>
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Backoff after a failed attempt, doubled each failure up to <see cref="MaxRetryDelay"/>. Default 5 minutes.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the retry backoff. Default 6 hours.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>Snapshot of the renewal service, also written to <c>status.json</c>.</summary>
public sealed class AcmeStatus
{
    /// <summary>Whether a certificate is currently stored.</summary>
    public bool HasCertificate { get; set; }

    /// <summary>Leaf expiry (UTC), or null.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Domains in the stored certificate.</summary>
    public string[] Domains { get; set; } = Array.Empty<string>();

    /// <summary>Directory URL in use.</summary>
    public string DirectoryUrl { get; set; } = string.Empty;

    /// <summary>When the last issuance attempt started (UTC), or null.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Whether the last attempt succeeded.</summary>
    public bool LastAttemptSucceeded { get; set; }

    /// <summary>Error text from the last failed attempt, or empty.</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>Consecutive failures since the last success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the service will next check (UTC).</summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    /// <summary>When the service last wrote this status (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Obtains and renews the certificate. Exactly one process per
/// deployment should run this; every process may read the resulting
/// files through a <see cref="CertificateWatcher"/>. The loop: ensure
/// the account, check the stored certificate, renew when it is missing,
/// within <see cref="AcmeOptions.RenewBefore"/> of expiry, covers
/// different domains, or a renew-now marker was dropped; on failure,
/// back off exponentially and keep the old certificate in place.
/// </summary>
public sealed class AcmeRenewalService
{
    private readonly AcmeOptions options;
    private readonly CertificateStore store;
    private readonly Action<string>? log;
    private readonly Func<AccountKey, AcmeClient> clientFactory;
    private readonly AcmeStatus status = new();
    private readonly object gate = new();
    private int failures;

    /// <summary>Construct.</summary>
    /// <param name="options">Options.</param>
    /// <param name="store">Certificate store.</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="clientFactory">Optional factory (tests inject an HTTP client); defaults to a real <see cref="AcmeClient"/>.</param>
    public AcmeRenewalService(AcmeOptions options, CertificateStore store, Action<string>? log = null, Func<AccountKey, AcmeClient>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        if (options.Domains.Count == 0)
        {
            throw new ArgumentException("At least one domain is required.", nameof(options));
        }
        this.options = options;
        this.store = store;
        this.log = log;
        this.clientFactory = clientFactory ?? (key => new AcmeClient(options.DirectoryUrl, key, log));
        this.status.DirectoryUrl = options.DirectoryUrl;
        this.RefreshStatusFromStore();
    }

    /// <summary>The current status snapshot.</summary>
    public AcmeStatus Status
    {
        get
        {
            lock (this.gate)
            {
                return new AcmeStatus
                {
                    HasCertificate = this.status.HasCertificate,
                    ExpiresAt = this.status.ExpiresAt,
                    Domains = this.status.Domains,
                    DirectoryUrl = this.status.DirectoryUrl,
                    LastAttemptAt = this.status.LastAttemptAt,
                    LastAttemptSucceeded = this.status.LastAttemptSucceeded,
                    LastError = this.status.LastError,
                    ConsecutiveFailures = this.status.ConsecutiveFailures,
                    NextCheckAt = this.status.NextCheckAt,
                    UpdatedAt = this.status.UpdatedAt,
                };
            }
        }
    }

    /// <summary>
    /// Whether the stored certificate needs replacing: missing, expiring
    /// within <see cref="AcmeOptions.RenewBefore"/>, or issued for a
    /// different set of domains.
    /// </summary>
    public bool NeedsRenewal()
    {
        using X509Certificate2? cert = this.store.LoadCertificate();
        if (cert is null)
        {
            return true;
        }
        if (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow < this.options.RenewBefore)
        {
            return true;
        }
        CertificateMetadata? meta = this.store.LoadMetadata();
        if (meta is null)
        {
            return false;
        }
        var have = new HashSet<string>(meta.Domains, StringComparer.OrdinalIgnoreCase);
        foreach (string d in this.options.Domains)
        {
            if (!have.Contains(d))
            {
                return true;
            }
        }
        return have.Count != this.options.Domains.Count;
    }

    /// <summary>
    /// Run one full issuance now, regardless of expiry. Returns the new
    /// certificate. Throws <see cref="AcmeException"/> on failure; the
    /// previous certificate stays in place.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public async Task<X509Certificate2> RenewNowAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.status.LastAttemptAt = DateTimeOffset.UtcNow;
        }
        try
        {
            X509Certificate2 cert = await this.IssueAsync(ct).ConfigureAwait(false);
            lock (this.gate)
            {
                this.failures = 0;
                this.status.LastAttemptSucceeded = true;
                this.status.LastError = string.Empty;
                this.status.ConsecutiveFailures = 0;
            }
            this.RefreshStatusFromStore();
            return cert;
        }
        catch (Exception ex) when (ex is AcmeException or HttpRequestException or IOException or CryptographicException or TaskCanceledException)
        {
            lock (this.gate)
            {
                this.failures++;
                this.status.LastAttemptSucceeded = false;
                this.status.LastError = ex.Message;
                this.status.ConsecutiveFailures = this.failures;
            }
            this.RefreshStatusFromStore();
            this.log?.Invoke($"ACME: issuance failed ({this.failures} consecutive): {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Run the renewal loop until cancelled. Safe to host in any process
    /// that serves <see cref="Http01ChallengeStore"/> on port 80 for the
    /// configured domains.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        this.log?.Invoke($"ACME: renewal service for {string.Join(", ", this.options.Domains)} via {this.options.DirectoryUrl} ({this.options.KeyType}).");
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = this.options.CheckInterval;
            bool forced = this.store.TakeRenewalRequest();
            if (forced)
            {
                this.log?.Invoke("ACME: renew-now request found.");
            }
            if (forced || this.NeedsRenewal())
            {
                try
                {
                    X509Certificate2 cert = await this.RenewNowAsync(ct).ConfigureAwait(false);
                    this.log?.Invoke($"ACME: certificate issued, expires {cert.NotAfter.ToUniversalTime():yyyy-MM-dd}.");
                    cert.Dispose();
                }
                catch (Exception ex) when (ex is AcmeException or HttpRequestException or IOException or CryptographicException)
                {
                    wait = this.RetryDelay();
                    this.log?.Invoke($"ACME: next attempt in {wait}.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (TaskCanceledException)
                {
                    wait = this.RetryDelay();
                    this.log?.Invoke($"ACME: timed out, next attempt in {wait}.");
                }
            }
            lock (this.gate)
            {
                this.status.NextCheckAt = DateTimeOffset.UtcNow + wait;
            }
            this.RefreshStatusFromStore();
            // Poll for a renew-now marker more often than the full check interval.
            TimeSpan slice = TimeSpan.FromSeconds(30);
            DateTimeOffset until = DateTimeOffset.UtcNow + wait;
            while (DateTimeOffset.UtcNow < until && !ct.IsCancellationRequested)
            {
                TimeSpan remaining = until - DateTimeOffset.UtcNow;
                try
                {
                    await Task.Delay(remaining < slice ? remaining : slice, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                if (File.Exists(this.store.RenewRequestPath))
                {
                    break;
                }
            }
        }
    }

    private TimeSpan RetryDelay()
    {
        int n;
        lock (this.gate)
        {
            n = Math.Max(1, this.failures);
        }
        double factor = Math.Pow(2, Math.Min(n - 1, 10));
        TimeSpan delay = TimeSpan.FromTicks((long)(this.options.InitialRetryDelay.Ticks * factor));
        return delay > this.options.MaxRetryDelay ? this.options.MaxRetryDelay : delay;
    }

    private async Task<X509Certificate2> IssueAsync(CancellationToken ct)
    {
        using AccountKey accountKey = this.store.LoadOrCreateAccountKey();
        using AcmeClient client = this.clientFactory(accountKey);
        await client.EnsureAccountAsync(this.options.ContactEmail, ct).ConfigureAwait(false);

        AcmeOrder order = await client.NewOrderAsync(this.options.Domains, ct).ConfigureAwait(false);
        this.log?.Invoke($"ACME: order {order.Url} ({order.Status}).");

        var tokens = new List<string>();
        try
        {
            foreach (string authzUrl in order.Authorizations)
            {
                AcmeAuthorization authz = await client.GetAuthorizationAsync(authzUrl, ct).ConfigureAwait(false);
                if (authz.Status == "valid")
                {
                    continue;
                }
                AcmeChallenge http01 = authz.Challenges.FirstOrDefault(c => c.Type == "http-01")
                    ?? throw new AcmeException($"No http-01 challenge offered for {authz.Identifier}.");
                Http01ChallengeStore.Add(http01.Token, client.KeyAuthorization(http01.Token));
                tokens.Add(http01.Token);
                await client.RespondToChallengeAsync(http01.Url, ct).ConfigureAwait(false);
                await this.WaitForChallengeAsync(client, http01.Url, authz.Identifier, ct).ConfigureAwait(false);
            }

            using AsymmetricAlgorithm certKey = CertificateStore.CreateCertificateKey(this.options.KeyType);
            byte[] csr = AcmeClient.BuildCsr(this.options.Domains, certKey);
            order = await client.FinalizeAsync(order.Finalize, csr, ct).ConfigureAwait(false);
            order = await this.WaitForOrderAsync(client, order, ct).ConfigureAwait(false);

            string chain = await client.DownloadCertificateAsync(order.Certificate, ct).ConfigureAwait(false);
            using X509Certificate2 leaf = X509Certificate2.CreateFromPem(chain);
            this.store.Save(certKey, chain, new CertificateMetadata
            {
                DirectoryUrl = this.options.DirectoryUrl,
                Domains = this.options.Domains.ToArray(),
                KeyType = this.options.KeyType.ToString(),
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = leaf.NotAfter.ToUniversalTime(),
            });
            return this.store.LoadCertificate() ?? throw new AcmeException("Saved certificate could not be reloaded.");
        }
        finally
        {
            foreach (string t in tokens)
            {
                Http01ChallengeStore.Remove(t);
            }
        }
    }

    private async Task WaitForChallengeAsync(AcmeClient client, string challengeUrl, string identifier, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + this.options.ValidationTimeout;
        TimeSpan delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            AcmeChallenge c = await client.GetChallengeAsync(challengeUrl, ct).ConfigureAwait(false);
            if (c.Status == "valid")
            {
                this.log?.Invoke($"ACME: {identifier} validated.");
                return;
            }
            if (c.Status == "invalid")
            {
                throw new AcmeException($"Challenge for {identifier} failed: {c.Error}");
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcmeException($"Challenge for {identifier} did not complete within {this.options.ValidationTimeout}.");
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(5))
            {
                delay += TimeSpan.FromSeconds(1);
            }
        }
    }

    private async Task<AcmeOrder> WaitForOrderAsync(AcmeClient client, AcmeOrder order, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + this.options.ValidationTimeout;
        while (order.Status != "valid")
        {
            if (order.Status == "invalid")
            {
                throw new AcmeException("Order became invalid after finalize.");
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcmeException($"Order did not become valid within {this.options.ValidationTimeout}.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            order = await client.GetOrderAsync(order.Url, ct).ConfigureAwait(false);
        }
        if (order.Certificate.Length == 0)
        {
            throw new AcmeException("Valid order has no certificate URL.");
        }
        return order;
    }

    private void RefreshStatusFromStore()
    {
        using X509Certificate2? cert = this.store.LoadCertificate();
        CertificateMetadata? meta = this.store.LoadMetadata();
        AcmeStatus snapshot;
        lock (this.gate)
        {
            this.status.HasCertificate = cert is not null;
            this.status.ExpiresAt = cert?.NotAfter.ToUniversalTime();
            this.status.Domains = meta?.Domains ?? Array.Empty<string>();
            this.status.UpdatedAt = DateTimeOffset.UtcNow;
            snapshot = this.Status;
        }
        try
        {
            this.store.WriteStatus(snapshot);
        }
        catch (IOException)
        {
            // Status file is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Status file is best-effort.
        }
    }
}

/// <summary>
/// Minimal HTTP listener that answers HTTP-01 challenges from
/// <see cref="Http01ChallengeStore"/> and nothing else. Used by a
/// deployment that hosts renewal in <c>Anjal.Server</c> (no webmail) and
/// therefore has no Kestrel on port 80. Every other path gets 404.
/// </summary>
public sealed class Http01Listener : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="bindAddress">Address to bind, e.g. "+" for all or "127.0.0.1".</param>
    /// <param name="port">Port, normally 80.</param>
    /// <param name="log">Optional log sink.</param>
    public Http01Listener(string bindAddress, int port, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        this.listener.Prefixes.Add($"http://{bindAddress}:{port}/");
        this.log = log;
    }

    /// <summary>Serve until cancelled.</summary>
    /// <param name="ct">Cancellation.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        this.listener.Start();
        using CancellationTokenRegistration reg = ct.Register(() => this.listener.Stop());
        this.log?.Invoke("ACME: HTTP-01 responder listening.");
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => Handle(ctx), CancellationToken.None);
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            string? ka = Http01ChallengeStore.Lookup(ctx.Request.Url?.AbsolutePath ?? string.Empty);
            if (ka is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            byte[] bytes = Encoding.ASCII.GetBytes(ka);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch (HttpListenerException)
        {
            // Client went away.
        }
        catch (IOException)
        {
            // Client went away.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.listener.IsListening)
        {
            this.listener.Stop();
        }
        this.listener.Close();
    }
}
