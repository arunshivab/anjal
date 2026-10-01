using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.9.2 (DEF-084): the webmail offers browsers TLS 1.3, or TLS 1.2
/// with ECDHE and GCM or ChaCha20 only. A client that offers nothing but the
/// two CBC suites SSL Labs marked weak is refused; a modern client connects.
/// A cipher policy can be set only on Linux, where production runs; elsewhere
/// the handshake tests have nothing to prove and return early.
/// </summary>
public sealed class Rc92WebmailCipherTests : System.IDisposable
{
    private static readonly TlsCipherSuite[] CbcOnly =
    {
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
    };

    private static readonly TlsCipherSuite[] GcmOnly =
    {
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
    };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc92-tls-" + System.Guid.NewGuid().ToString("N"));

    public Rc92WebmailCipherTests()
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
    public void WebmailSuites_HaveNoCbcNoDheAndNoStaticRsa()
    {
        Assert.NotEmpty(Anjal.Smtp.TlsCipherSet.WebmailSuites);
        foreach (TlsCipherSuite s in Anjal.Smtp.TlsCipherSet.WebmailSuites)
        {
            string name = s.ToString();
            Assert.DoesNotContain("CBC", name, System.StringComparison.Ordinal);
            Assert.False(name.StartsWith("TLS_DHE_", System.StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("TLS_RSA_", System.StringComparison.Ordinal), name);
        }
        Assert.All(CbcOnly, s => Assert.DoesNotContain(s, Anjal.Smtp.TlsCipherSet.WebmailSuites));
    }

    [Fact]
    public async Task Https_RefusesCbcOnlyClients_AndServesModernOnes()
    {
        if (!System.OperatingSystem.IsLinux())
        {
            return;
        }
        string certPath = System.IO.Path.Combine(this.root, "cert.pem");
        string keyPath = System.IO.Path.Combine(this.root, "key.pem");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var req = new CertificateRequest("CN=localhost", ec, HashAlgorithmName.SHA256);
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
            Assert.Null(await HandshakeAsync(httpsPort, System.Security.Authentication.SslProtocols.Tls12, CbcOnly));
            TlsCipherSuite? gcm = await HandshakeAsync(httpsPort, System.Security.Authentication.SslProtocols.Tls12, GcmOnly);
            Assert.Equal(TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, gcm);
            TlsCipherSuite? tls13 = await HandshakeAsync(httpsPort, System.Security.Authentication.SslProtocols.Tls13, null);
            Assert.NotNull(tls13);
            Assert.Contains(tls13!.Value, Anjal.Smtp.TlsCipherSet.WebmailSuites);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>One TLS handshake; the negotiated suite, or null when the server refuses it.</summary>
    private static async Task<TlsCipherSuite?> HandshakeAsync(int port, System.Security.Authentication.SslProtocols protocols, TlsCipherSuite[]? suites)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
#pragma warning disable CA5359 // A self-signed test certificate: only the cipher negotiation is under test.
        using var ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) => true);
#pragma warning restore CA5359
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = protocols,
        };
        if (suites is not null && System.OperatingSystem.IsLinux())
        {
            options.CipherSuitesPolicy = new CipherSuitesPolicy(suites);
        }
        try
        {
            await ssl.AuthenticateAsClientAsync(options).ConfigureAwait(false);
            return ssl.NegotiatedCipherSuite;
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            return null;
        }
        catch (System.IO.IOException)
        {
            return null;
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
