using System.Net;
using System.Security.Cryptography.X509Certificates;
using Anjal.Acme;
using Anjal.Acme.Tests;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;

namespace Anjal.Webmail.Tests;

/// <summary>
/// Boots the real webmail with HTTPS enabled and ACME hosted in-process
/// against the fake CA: the webmail must answer the HTTP-01 challenge on
/// its own HTTP listener, obtain the certificate, serve HTTPS with it,
/// redirect HTTP to HTTPS, send HSTS, and pick up a renewed certificate
/// without restarting.
/// </summary>
public sealed class WebmailTlsTests : IAsyncLifetime, System.IDisposable
{
    private static readonly string[] Domains = new[] { "localhost" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-webmail-tls-" + System.Guid.NewGuid().ToString("N"));
    private readonly string acmeDir;
    private readonly InMemoryMessageStore store = new();
    private readonly int httpPort;
    private readonly int httpsPort;
    private FakeAcmeServer ca = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private HttpClient https = null!;

    public WebmailTlsTests()
    {
        this.acmeDir = System.IO.Path.Combine(this.root, "acme");
        this.httpPort = FreePort();
        this.httpsPort = FreePort();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        this.ca = new FakeAcmeServer(this.httpPort);
        this.ca.Start();

        var env = new AcmeEnvironment
        {
            Directory = this.acmeDir,
            Domains = Domains,
            Host = true,
            Options = new AcmeOptions
            {
                DirectoryUrl = this.ca.DirectoryUrl,
                Domains = Domains,
                ValidationTimeout = System.TimeSpan.FromSeconds(20),
                CheckInterval = System.TimeSpan.FromMilliseconds(200),
                InitialRetryDelay = System.TimeSpan.FromMilliseconds(200),
            },
        };
        this.app = Program.CreateApp(System.Array.Empty<string>(), this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), "anjal.localhost",
            $"http://127.0.0.1:{this.httpPort}", new Program.TlsSettings { HttpsPort = this.httpsPort, Acme = env });
        await this.app.StartAsync();

        this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new System.Uri($"http://127.0.0.1:{this.httpPort}/") };
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(this.ca.Root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return cert is not null && chain.Build(new X509Certificate2(cert));
            },
        };
        this.https = new HttpClient(handler) { BaseAddress = new System.Uri($"https://localhost:{this.httpsPort}/") };
    }

    public void Dispose()
    {
        this.http?.Dispose();
        this.https?.Dispose();
    }

    public async System.Threading.Tasks.Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        this.ca.Dispose();
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async System.Threading.Tasks.Task WaitForCertificateAsync(string? differentFrom = null)
    {
        var store = new CertificateStore(this.acmeDir);
        System.DateTime deadline = System.DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using X509Certificate2? c = store.LoadCertificate();
            if (c is not null && c.Thumbprint != differentFrom)
            {
                return;
            }
            if (System.DateTime.UtcNow > deadline)
            {
                throw new System.TimeoutException("Certificate was not issued in time.");
            }
            await System.Threading.Tasks.Task.Delay(100);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ObtainsCertificate_ServesHttps_RedirectsHttp_Hsts_AndHotReloads()
    {
        // Before any certificate exists, HTTP serves the login page directly (no redirect).
        HttpResponseMessage early = await this.http.GetAsync("login");
        Assert.True(early.StatusCode is HttpStatusCode.OK or HttpStatusCode.MovedPermanently);

        await this.WaitForCertificateAsync();
        Assert.Equal(1, this.ca.Issued);

        // HTTPS works with the issued certificate.
        HttpResponseMessage secure = await this.https.GetAsync("login");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Contains("<h1>Anjal</h1>", await secure.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        Assert.True(secure.Headers.TryGetValues("Strict-Transport-Security", out System.Collections.Generic.IEnumerable<string>? hsts));
        Assert.Contains("max-age=31536000", hsts!.First(), System.StringComparison.Ordinal);

        // HTTP now redirects to HTTPS - except challenge paths, which are still answered.
        HttpResponseMessage redirect = await this.http.GetAsync("login?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal($"https://127.0.0.1:{this.httpsPort}/login?x=1", redirect.Headers.Location!.ToString());
        Http01ChallengeStore.Add("t-live", "t-live.ka");
        try
        {
            HttpResponseMessage challenge = await this.http.GetAsync("/.well-known/acme-challenge/t-live");
            Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
            Assert.Equal("t-live.ka", await challenge.Content.ReadAsStringAsync());
        }
        finally
        {
            Http01ChallengeStore.Remove("t-live");
        }

        // Force a renewal through the marker file and confirm the new certificate is served.
        string firstThumb;
        using (X509Certificate2 first = new CertificateStore(this.acmeDir).LoadCertificate()!)
        {
            firstThumb = first.Thumbprint;
        }
        await System.Threading.Tasks.Task.Delay(1100); // distinct mtime on coarse filesystems
        new CertificateStore(this.acmeDir).RequestRenewal();
        await this.WaitForCertificateAsync(differentFrom: firstThumb);
        Assert.Equal(2, this.ca.Issued);

        CertificateWatcher watcher = (CertificateWatcher)this.app.Services.GetService(typeof(CertificateWatcher))!;
        watcher.Invalidate();
        string? servedThumb = null;
        using (var probe = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                servedThumb = cert?.GetCertHashString();
                return true;
            },
        }))
        {
            HttpResponseMessage again = await probe.GetAsync($"https://localhost:{this.httpsPort}/login");
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
        Assert.NotNull(servedThumb);
        Assert.NotEqual(firstThumb, servedThumb);
    }
}
