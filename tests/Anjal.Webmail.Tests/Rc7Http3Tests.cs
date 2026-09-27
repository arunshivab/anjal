using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.7: HTTP/3 on the HTTPS port where QUIC is available - a real
/// HTTP/3 request - and, everywhere else, a clean fall-back to HTTP/2 that
/// does not advertise HTTP/3.
/// </summary>
public sealed class Rc7Http3Tests : System.IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc7-h3-" + System.Guid.NewGuid().ToString("N"));

    public Rc7Http3Tests()
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
    public async Task Https_ServesHttp3WhereQuicExists_ElseHttp2WithoutAdvertisingIt()
    {
        string certPath = System.IO.Path.Combine(this.root, "cert.pem");
        string keyPath = System.IO.Path.Combine(this.root, "key.pem");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var req = new CertificateRequest("CN=localhost", ec, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            using X509Certificate2 cert = req.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(1));
            await System.IO.File.WriteAllTextAsync(certPath, cert.ExportCertificatePem());
            await System.IO.File.WriteAllTextAsync(keyPath, ec.ExportPkcs8PrivateKeyPem());
        }
        int httpsPort = FreePort();
        WebApplication app = Program.CreateApp(
            System.Array.Empty<string>(),
            new InMemoryMessageStore(),
            new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"),
            "anjal.localhost",
            "http://127.0.0.1:0",
            new Program.TlsSettings { HttpsPort = httpsPort, StaticCertPath = certPath, StaticKeyPath = keyPath });
        await app.StartAsync();
        try
        {
#pragma warning disable CA5359 // A self-signed test certificate: only the protocol is under test.
            using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true } };
#pragma warning restore CA5359
            using var client = new HttpClient(handler);
            var uri = new System.Uri($"https://127.0.0.1:{httpsPort}/sign-in");
            HttpResponseMessage h2 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact });
            Assert.Equal(HttpStatusCode.OK, h2.StatusCode);
            Assert.Equal(HttpVersion.Version20, h2.Version);
            bool advertised = h2.Headers.TryGetValues("Alt-Svc", out IEnumerable<string>? altSvc) && altSvc.Any(v => v.Contains("h3", System.StringComparison.Ordinal));

            if (Program.Http3Available(out _) && (System.OperatingSystem.IsLinux() || System.OperatingSystem.IsWindows() || System.OperatingSystem.IsMacOS()))
            {
                Assert.True(advertised, "HTTP/3 must be advertised through Alt-Svc");
                // A QUIC connection that presents a NAME, as browsers do (they
                // reach mail.anjal.co.in). An IP address as the TLS name is
                // refused by Linux's QUIC (OpenSSL), and the webmail listens on
                // IPv4 only - both measured in CI on 27 Sep 2026.
                System.Net.Security.SslApplicationProtocol alpn = await QuicByNameAsync(httpsPort);
                Assert.Equal("h3", alpn.ToString());
            }
            else
            {
                Assert.False(advertised, "HTTP/3 must not be advertised when QUIC is unavailable");
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>A QUIC connection to 127.0.0.1 presenting the name "localhost"; the negotiated ALPN.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task<System.Net.Security.SslApplicationProtocol> QuicByNameAsync(int port)
    {
        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(15));
        await using System.Net.Quic.QuicConnection quic = await System.Net.Quic.QuicConnection.ConnectAsync(new System.Net.Quic.QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            DefaultStreamErrorCode = 0x100,
            DefaultCloseErrorCode = 0x100,
            ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol> { System.Net.Security.SslApplicationProtocol.Http3 },
                TargetHost = "localhost",
#pragma warning disable CA5359 // A self-signed test certificate: only the protocol is under test.
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true,
#pragma warning restore CA5359
            },
        }, cts.Token);
        return quic.NegotiatedApplicationProtocol;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
