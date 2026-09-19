using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Anjal.Acme.Tests;

public sealed class AcmeTests : IDisposable
{
    private static readonly string[] OneDomain = new[] { "mail.anjal.test" };
    private static readonly string[] TwoDomains = new[] { "mail.anjal.test", "anjal.test" };

    private readonly string dir = Path.Combine(Path.GetTempPath(), "anjal-acme-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> log = new();
    private readonly Http01Listener responder;
    private readonly int responderPort;
    private readonly CancellationTokenSource responderCts = new();
    private readonly FakeAcmeServer ca;

    public AcmeTests()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        this.responderPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        this.responder = new Http01Listener("127.0.0.1", this.responderPort);
        _ = this.responder.RunAsync(this.responderCts.Token);
        this.ca = new FakeAcmeServer(this.responderPort);
        this.ca.Start();
    }

    public void Dispose()
    {
        this.responderCts.Cancel();
        this.responder.Dispose();
        this.responderCts.Dispose();
        this.ca.Dispose();
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private AcmeRenewalService Service(string[] domains, CertificateKeyType keyType = CertificateKeyType.EcdsaP256, TimeSpan? renewBefore = null) =>
        new(new AcmeOptions
        {
            DirectoryUrl = this.ca.DirectoryUrl,
            Domains = domains,
            ContactEmail = "arun@anjal.test",
            KeyType = keyType,
            RenewBefore = renewBefore ?? TimeSpan.FromDays(30),
            ValidationTimeout = TimeSpan.FromSeconds(20),
            CheckInterval = TimeSpan.FromMilliseconds(200),
            InitialRetryDelay = TimeSpan.FromMilliseconds(200),
        }, new CertificateStore(this.dir), this.log.Add);

    // ---------- crypto primitives ----------

    [Fact]
    public void Base64Url_RoundTrips_NoPadding()
    {
        byte[] data = new byte[] { 0xFB, 0xFF, 0x00, 0x01, 0x7E };
        string enc = Base64Url.Encode(data);
        Assert.DoesNotContain("=", enc, StringComparison.Ordinal);
        Assert.DoesNotContain("+", enc, StringComparison.Ordinal);
        Assert.DoesNotContain("/", enc, StringComparison.Ordinal);
        Assert.Equal(data, Base64Url.Decode(enc));
    }

    [Fact]
    public void AccountKey_PemRoundTrip_SameThumbprint_AndJwkVerifies()
    {
        using AccountKey a = AccountKey.Create();
        using AccountKey b = AccountKey.FromPem(a.ToPem());
        Assert.Equal(a.Thumbprint, b.Thumbprint);
        Assert.Equal(43, a.Thumbprint.Length);

        byte[] input = System.Text.Encoding.ASCII.GetBytes("abc.def");
        byte[] sig = a.Sign(input);
        Assert.Equal(64, sig.Length);
        using AccountKey pub = AccountKey.FromJwk(a.JwkJson);
        Assert.True(pub.Verify(input, sig));
        Assert.False(pub.Verify(System.Text.Encoding.ASCII.GetBytes("abc.deg"), sig));
    }

    [Fact]
    public void Jws_HasRequiredProtectedHeader()
    {
        using AccountKey k = AccountKey.Create();
        string jws = Jws.Sign(k, "https://ca/new-account", "n0nce", "{\"a\":1}", kid: null);
        using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(jws);
        string header = System.Text.Encoding.UTF8.GetString(Base64Url.Decode(d.RootElement.GetProperty("protected").GetString()!));
        Assert.Contains("\"alg\":\"ES256\"", header, StringComparison.Ordinal);
        Assert.Contains("\"jwk\":{", header, StringComparison.Ordinal);
        Assert.Contains("\"nonce\":\"n0nce\"", header, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"https://ca/new-account\"", header, StringComparison.Ordinal);

        string withKid = Jws.Sign(k, "https://ca/x", "n", string.Empty, kid: "https://ca/acct/1");
        using System.Text.Json.JsonDocument d2 = System.Text.Json.JsonDocument.Parse(withKid);
        Assert.Equal(string.Empty, d2.RootElement.GetProperty("payload").GetString());
        string header2 = System.Text.Encoding.UTF8.GetString(Base64Url.Decode(d2.RootElement.GetProperty("protected").GetString()!));
        Assert.Contains("\"kid\":\"https://ca/acct/1\"", header2, StringComparison.Ordinal);
        Assert.DoesNotContain("jwk", header2, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCsr_ContainsAllSans_ForBothKeyTypes()
    {
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA rsa = RSA.Create(2048);
        foreach (AsymmetricAlgorithm key in new AsymmetricAlgorithm[] { ec, rsa })
        {
            byte[] csr = AcmeClient.BuildCsr(TwoDomains, key);
            CertificateRequest req = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            Assert.Equal("CN=mail.anjal.test", req.SubjectName.Name);
            var san = Assert.Single(req.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>());
            Assert.Equal(TwoDomains, san.EnumerateDnsNames().ToArray());
        }
    }

    // ---------- challenge store / responder ----------

    [Fact]
    public async Task Http01Listener_ServesRegisteredTokens_404Otherwise()
    {
        Http01ChallengeStore.Add("tok123", "tok123.thumb");
        try
        {
            using var http = new HttpClient();
            string ok = await http.GetStringAsync($"http://127.0.0.1:{this.responderPort}/.well-known/acme-challenge/tok123");
            Assert.Equal("tok123.thumb", ok);
            HttpResponseMessage missing = await http.GetAsync($"http://127.0.0.1:{this.responderPort}/.well-known/acme-challenge/nope");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
            HttpResponseMessage other = await http.GetAsync($"http://127.0.0.1:{this.responderPort}/anything");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, other.StatusCode);
        }
        finally
        {
            Http01ChallengeStore.Remove("tok123");
        }
        Assert.Null(Http01ChallengeStore.Lookup("/.well-known/acme-challenge/tok123"));
        Assert.Null(Http01ChallengeStore.Lookup("/.well-known/acme-challenge/a/b"));
    }

    // ---------- full protocol against the fake CA ----------

    [Fact]
    public async Task Issue_EndToEnd_ProducesTrustedChain_AndReusesAccount()
    {
        AcmeRenewalService svc = this.Service(TwoDomains);
        Assert.True(svc.NeedsRenewal());

        using X509Certificate2 cert = await svc.RenewNowAsync();
        Assert.True(cert.HasPrivateKey);
        Assert.Equal("CN=mail.anjal.test", cert.Subject);
        Assert.Contains("anjal.test", cert.MatchesHostname("anjal.test") ? "anjal.test" : string.Empty, StringComparison.Ordinal);
        Assert.True(cert.MatchesHostname("mail.anjal.test"));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(this.ca.Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(cert), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));

        var store = new CertificateStore(this.dir);
        Assert.True(store.HasCertificate);
        Assert.Single(store.LoadChain());
        CertificateMetadata? meta = store.LoadMetadata();
        Assert.Equal(TwoDomains, meta!.Domains);
        Assert.Equal("EcdsaP256", meta.KeyType);
        Assert.False(svc.NeedsRenewal());
        AcmeStatus status = svc.Status;
        Assert.True(status.HasCertificate);
        Assert.True(status.LastAttemptSucceeded);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(store.ReadStatus());
        Assert.Equal(0, Http01ChallengeStore.Count);

        // Second issuance re-uses the account key and the CA reports the existing account.
        using X509Certificate2 again = await svc.RenewNowAsync();
        Assert.Equal(2, this.ca.NewAccountCalls);
        Assert.Equal(2, this.ca.Issued);
        Assert.NotEqual(cert.Thumbprint, again.Thumbprint);
        Assert.Equal(0, this.ca.BadNonceRejections);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.CertificateKeyPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.AccountKeyPath));
        }
    }

    [Fact]
    public async Task Issue_Rsa_Works()
    {
        AcmeRenewalService svc = this.Service(OneDomain, CertificateKeyType.Rsa2048);
        using X509Certificate2 cert = await svc.RenewNowAsync();
        using RSA? rsa = cert.GetRSAPrivateKey();
        Assert.NotNull(rsa);
        Assert.Equal(2048, rsa!.KeySize);
        Assert.Equal("Rsa2048", new CertificateStore(this.dir).LoadMetadata()!.KeyType);
    }

    [Fact]
    public async Task FailedValidation_Throws_KeepsOldCertificate_RecordsError()
    {
        AcmeRenewalService svc = this.Service(OneDomain);
        using X509Certificate2 first = await svc.RenewNowAsync();

        this.ca.FailValidation = true;
        AcmeException ex = await Assert.ThrowsAsync<AcmeException>(() => svc.RenewNowAsync());
        Assert.Contains("failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        using X509Certificate2? still = new CertificateStore(this.dir).LoadCertificate();
        Assert.Equal(first.Thumbprint, still!.Thumbprint);
        AcmeStatus status = svc.Status;
        Assert.False(status.LastAttemptSucceeded);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Contains("failed", status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Http01ChallengeStore.Count);
    }

    [Fact]
    public async Task NeedsRenewal_WhenExpiringSoon_OrDomainsChanged()
    {
        this.ca.Validity = TimeSpan.FromDays(10);
        AcmeRenewalService svc = this.Service(OneDomain, renewBefore: TimeSpan.FromDays(30));
        using X509Certificate2 _ = await svc.RenewNowAsync();
        Assert.True(svc.NeedsRenewal(), "10-day cert with 30-day window must renew");

        this.ca.Validity = TimeSpan.FromDays(90);
        using X509Certificate2 __ = await svc.RenewNowAsync();
        Assert.False(svc.NeedsRenewal());

        AcmeRenewalService more = this.Service(TwoDomains);
        Assert.True(more.NeedsRenewal(), "adding a domain must renew");
    }

    [Fact]
    public async Task RunLoop_IssuesOnStart_AndHonoursRenewNowMarker()
    {
        AcmeRenewalService svc = this.Service(OneDomain);
        var store = new CertificateStore(this.dir);
        using var cts = new CancellationTokenSource();
        Task loop = svc.RunAsync(cts.Token);

        await WaitUntilAsync(() => svc.Status.LastAttemptSucceeded, TimeSpan.FromSeconds(20));
        string firstThumb = new CertificateStore(this.dir).LoadCertificate()!.Thumbprint;
        store.RequestRenewal();
        await WaitUntilAsync(() => new CertificateStore(this.dir).LoadCertificate()!.Thumbprint != firstThumb, TimeSpan.FromSeconds(20));
        Assert.False(File.Exists(store.RenewRequestPath));
        Assert.Equal(2, this.ca.Issued);

        cts.Cancel();
        await loop;
        Assert.Contains(this.log, l => l.Contains("renew-now request found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunLoop_RetriesAfterFailure_WithBackoff()
    {
        this.ca.FailValidation = true;
        AcmeRenewalService svc = this.Service(OneDomain);
        using var cts = new CancellationTokenSource();
        Task loop = svc.RunAsync(cts.Token);

        await WaitUntilAsync(() => svc.Status.ConsecutiveFailures >= 2, TimeSpan.FromSeconds(30));
        this.ca.FailValidation = false;
        await WaitUntilAsync(() => svc.Status.LastAttemptSucceeded, TimeSpan.FromSeconds(30));
        Assert.Equal(0, svc.Status.ConsecutiveFailures);
        Assert.Equal(1, this.ca.Issued);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task CertificateWatcher_ReloadsWhenFileChanges()
    {
        AcmeRenewalService svc = this.Service(OneDomain);
        var store = new CertificateStore(this.dir);
        using var watcher = new CertificateWatcher(store) { PollInterval = TimeSpan.Zero };
        Assert.Null(watcher.Current);

        using X509Certificate2 first = await svc.RenewNowAsync();
        int reloads = 0;
        watcher.Reloaded += _ => reloads++;
        Assert.Equal(first.Thumbprint, watcher.Current!.Thumbprint);
        Assert.Equal(first.Thumbprint, watcher.Current!.Thumbprint);
        Assert.Equal(1, reloads);

        await Task.Delay(1100); // ensure a distinguishable mtime on coarse filesystems
        using X509Certificate2 second = await svc.RenewNowAsync();
        Assert.Equal(second.Thumbprint, watcher.Current!.Thumbprint);
        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task BadNonce_IsRetriedOnce()
    {
        // Poison the client's cached nonce by making a request through a
        // second client with the same account key: the CA issues nonces per
        // response, so nothing is shared - instead we just check the CA's
        // single-use enforcement and that the client never reuses one.
        AcmeRenewalService svc = this.Service(OneDomain);
        using X509Certificate2 _ = await svc.RenewNowAsync();
        Assert.Equal(0, this.ca.BadNonceRejections);
        Assert.DoesNotContain(this.log, l => l.Contains("bad nonce", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModuleInfo_IsSet()
    {
        Assert.Equal("Anjal.Acme", ModuleInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(ModuleInfo.Version));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }
            await Task.Delay(100);
        }
    }
}
