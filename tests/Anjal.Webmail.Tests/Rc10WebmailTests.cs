using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.10: pages are never cached by the browser (DEF-078), and the
/// MTA-STS policy is served exactly as RFC 8461 requires (D-63).
/// </summary>
public sealed class Rc10WebmailTests : System.IDisposable
{
    private static readonly string[] Domain = new[] { "example.test" };
    private static readonly string[] OneMx = new[] { "mail.example.test" };
    private static readonly string[] TwoMx = new[] { "mail.example.test", "backup.example.test" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc10-web-" + System.Guid.NewGuid().ToString("N"));

    public Rc10WebmailTests()
    {
        System.IO.Directory.CreateDirectory(this.root);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task PagesAreNotStored_AndAPageRestoredByBack_Reloads()
    {
        // DEF-078: pages already carry no-store (the anti-forgery system sets it on
        // every page with a form), yet browsers restored a stale folder list from
        // their back-forward cache. The fix is app.js reloading such a page; its
        // proof in a real browser is the owner's check after the upgrade.
        int port = FreePort();
        WebApplication app = Program.CreateApp(System.Array.Empty<string>(), new InMemoryMessageStore(),
            new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), "anjal.localhost", $"http://127.0.0.1:{port}", null);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
            HttpResponseMessage page = await client.GetAsync("sign-in");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("no-store", page.Headers.CacheControl?.ToString() ?? string.Empty, System.StringComparison.Ordinal);
            HttpResponseMessage script = await client.GetAsync("app.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            string js = await script.Content.ReadAsStringAsync();
            Assert.Contains("addEventListener('pageshow'", js, System.StringComparison.Ordinal);
            Assert.Contains("event.persisted", js, System.StringComparison.Ordinal);
            Assert.Contains("window.location.reload()", js, System.StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void PolicyText_IsExactlyRfc8461()
    {
        var testing = new MtaStsPolicy { Mode = "testing", Domains = Domain, Mx = OneMx, MaxAgeSeconds = 86400 };
        Assert.Equal("version: STSv1\r\nmode: testing\r\nmx: mail.example.test\r\nmax_age: 86400\r\n", testing.Text());
        var enforce = new MtaStsPolicy { Mode = "enforce", Domains = Domain, Mx = TwoMx, MaxAgeSeconds = 604800 };
        Assert.Equal("version: STSv1\r\nmode: enforce\r\nmx: mail.example.test\r\nmx: backup.example.test\r\nmax_age: 604800\r\n", enforce.Text());
        Assert.True(testing.Serves("MTA-STS.Example.Test"));
        Assert.False(testing.Serves("example.test"));
        Assert.False(testing.Serves("mta-sts.other.test"));
    }

    [Fact]
    public async Task Policy_IsServedOverHttps_OnlyForItsHost_NeverRedirected()
    {
        string certPath = System.IO.Path.Combine(this.root, "cert.pem");
        string keyPath = System.IO.Path.Combine(this.root, "key.pem");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var req = new CertificateRequest("CN=mta-sts.example.test", ec, HashAlgorithmName.SHA256);
            using X509Certificate2 cert = req.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(1));
            await System.IO.File.WriteAllTextAsync(certPath, cert.ExportCertificatePem());
            await System.IO.File.WriteAllTextAsync(keyPath, ec.ExportPkcs8PrivateKeyPem());
        }
        int httpsPort = FreePort();
        var policy = new MtaStsPolicy { Mode = "testing", Domains = Domain, Mx = OneMx, MaxAgeSeconds = 86400 };
        WebApplication app = Program.CreateApp(System.Array.Empty<string>(), new InMemoryMessageStore(),
            new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), "mail.example.test", "http://127.0.0.1:0",
            new Program.TlsSettings { HttpsPort = httpsPort, StaticCertPath = certPath, StaticKeyPath = keyPath, MtaSts = policy });
        await app.StartAsync();
        try
        {
#pragma warning disable CA5359 // A self-signed test certificate: the policy response is under test.
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
#pragma warning restore CA5359
            using var client = new HttpClient(handler);
            var url = new System.Uri($"https://127.0.0.1:{httpsPort}{MtaStsPolicy.PolicyPath}");

            using var good = new HttpRequestMessage(HttpMethod.Get, url);
            good.Headers.Host = "mta-sts.example.test";
            HttpResponseMessage ok = await client.SendAsync(good);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("text/plain", ok.Content.Headers.ContentType?.MediaType);
            Assert.Equal(policy.Text(), await ok.Content.ReadAsStringAsync());

            using var other = new HttpRequestMessage(HttpMethod.Get, url);
            other.Headers.Host = "mail.example.test";
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(other)).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Policy_IsNotServed_WhenOff()
    {
        int port = FreePort();
        WebApplication app = Program.CreateApp(System.Array.Empty<string>(), new InMemoryMessageStore(),
            new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), "mail.example.test", $"http://127.0.0.1:{port}", null);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, new System.Uri($"http://127.0.0.1:{port}{MtaStsPolicy.PolicyPath}"));
            req.Headers.Host = "mta-sts.example.test";
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(req)).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
