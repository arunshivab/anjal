using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Store;

namespace Anjal.Smtp.Tests;

/// <summary>
/// v1.0.0-rc.7: DEF-064 (no cipher in common with DHE-only or RSA-only mail
/// servers) and the owner's rule of 27 Sep 2026 - never send mail unencrypted.
/// </summary>
public sealed class Rc7TlsTests
{
    [Theory]
    [InlineData(TlsMode.Opportunistic, true, false, false, TlsAction.StartTls)]
    [InlineData(TlsMode.Opportunistic, false, false, false, TlsAction.Hold)]
    [InlineData(TlsMode.Opportunistic, false, true, false, TlsAction.Plaintext)]
    [InlineData(TlsMode.Opportunistic, false, false, true, TlsAction.Plaintext)]
    [InlineData(TlsMode.Required, true, false, false, TlsAction.StartTls)]
    [InlineData(TlsMode.Required, false, true, false, TlsAction.Hold)]
    [InlineData(TlsMode.Required, false, true, true, TlsAction.Hold)]
    [InlineData(TlsMode.Disabled, true, false, false, TlsAction.StartTls)]
    [InlineData(TlsMode.Disabled, true, true, false, TlsAction.Plaintext)]
    [InlineData(TlsMode.Disabled, false, false, false, TlsAction.Hold)]
    [InlineData(TlsMode.Disabled, false, false, true, TlsAction.Plaintext)]
    public void Decide_NeverPlaintextUnlessPermitted(TlsMode mode, bool offered, bool allowPlaintext, bool loopback, TlsAction expected)
    {
        Assert.Equal(expected, TlsDecision.Decide(mode, offered, allowPlaintext, loopback));
    }

    [Fact]
    public void CipherSet_ModernFirst_DheThenRsaGcm_NoWeakSuites()
    {
        var suites = TlsCipherSet.OutboundSuites;
        Assert.Equal(TlsCipherSuite.TLS_AES_256_GCM_SHA384, suites[0]);
        Assert.Contains(TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384, suites);
        Assert.Contains(TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, suites);
        int dhe = IndexOf(suites, TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384);
        int ecdhe = IndexOf(suites, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384);
        int rsa = IndexOf(suites, TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384);
        Assert.True(ecdhe < dhe && dhe < rsa, "order must be ECDHE, then DHE, then static RSA");
        foreach (TlsCipherSuite s in suites)
        {
            string name = s.ToString();
            Assert.DoesNotContain("CBC", name, System.StringComparison.Ordinal);
            Assert.DoesNotContain("3DES", name, System.StringComparison.Ordinal);
            Assert.DoesNotContain("RC4", name, System.StringComparison.Ordinal);
            Assert.DoesNotContain("NULL", name, System.StringComparison.Ordinal);
            Assert.DoesNotContain("EXPORT", name, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ErrorChain_ShowsTheInnerCause()
    {
        var ex = new System.Security.Authentication.AuthenticationException(
            "Authentication failed, see inner exception.",
            new System.InvalidOperationException("SSL Handshake failed with OpenSSL error - SSL_ERROR_SSL."));
        string chain = TlsCipherSet.ErrorChain(ex);
        Assert.Equal("Authentication failed - SSL Handshake failed with OpenSSL error - SSL_ERROR_SSL", chain);
    }

    [Fact]
    public void RelayLoopback_OnlyThisMachine()
    {
        Assert.True(RelayMailSender.IsLoopback("127.0.0.1"));
        Assert.True(RelayMailSender.IsLoopback("localhost"));
        Assert.True(RelayMailSender.IsLoopback("::1"));
        Assert.False(RelayMailSender.IsLoopback("smtp.example.com"));
        Assert.False(RelayMailSender.IsLoopback("10.0.0.5"));
    }

    [Fact]
    public async System.Threading.Tasks.Task RsaOnlyServer_DefaultsFail_Rc7CipherSetConnects()
    {
        if (!System.OperatingSystem.IsLinux())
        {
            return; // A cipher policy can only be set on Linux; production runs there.
        }
        using X509Certificate2 cert = RsaCertificate();
        Assert.Null(await HandshakeAsync(cert, TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, clientPolicy: null));
        Assert.Equal(TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, await HandshakeAsync(cert, TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, TlsCipherSet.OutboundPolicy()));
    }

    [Fact]
    public async System.Threading.Tasks.Task DheOnlyServer_LikeRediff_Rc7CipherSetConnects()
    {
        // .NET cannot serve DHE, so this uses OpenSSL's test server - exactly
        // what rediffmail.com's MX offered on 27 Sep 2026 (DHE-RSA-AES256-GCM-SHA384).
        if (!System.OperatingSystem.IsLinux() || !CommandExists("openssl"))
        {
            return;
        }
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-dhe-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            using X509Certificate2 cert = RsaCertificate();
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, "c.pem"), cert.ExportCertificatePem());
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, "k.pem"), cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
            int port = FreePort();
            using var server = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("openssl")
            {
                ArgumentList = { "s_server", "-accept", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-cert", "c.pem", "-key", "k.pem", "-tls1_2", "-cipher", "DHE-RSA-AES256-GCM-SHA384", "-quiet" },
                WorkingDirectory = dir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            try
            {
                await WaitForPortAsync(port);
                Assert.Null(await ConnectAsync(port, clientPolicy: null));
                Assert.Equal(TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384, await ConnectAsync(port, TlsCipherSet.OutboundPolicy()));
            }
            finally
            {
                if (!server.HasExited)
                {
                    server.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<TlsCipherSuite> list, TlsCipherSuite s)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == s)
            {
                return i;
            }
        }
        return -1;
    }

    private static X509Certificate2 RsaCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=anjal-rc7-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 temp = req.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool CommandExists(string name)
    {
        foreach (string dir in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator))
        {
            if (dir.Length > 0 && System.IO.File.Exists(System.IO.Path.Combine(dir, name)))
            {
                return true;
            }
        }
        return false;
    }

    private static async System.Threading.Tasks.Task WaitForPortAsync(int port)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Handshake against an in-process .NET server offering one suite; the negotiated suite, or null on failure.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async System.Threading.Tasks.Task<TlsCipherSuite?> HandshakeAsync(X509Certificate2 cert, TlsCipherSuite serverOnly, CipherSuitesPolicy? clientPolicy)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        System.Threading.Tasks.Task server = System.Threading.Tasks.Task.Run(async () =>
        {
            using TcpClient c = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var s = new SslStream(c.GetStream());
            try
            {
                await s.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                    CipherSuitesPolicy = new CipherSuitesPolicy(new[] { serverOnly }),
                }).ConfigureAwait(false);
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // The failing case: no suite in common.
            }
            catch (System.IO.IOException)
            {
                // The client gave up first.
            }
        });
        try
        {
            return await ConnectAsync(port, clientPolicy).ConfigureAwait(false);
        }
        finally
        {
            await server.ConfigureAwait(false);
            listener.Stop();
        }
    }

    private static async System.Threading.Tasks.Task<TlsCipherSuite?> ConnectAsync(int port, CipherSuitesPolicy? clientPolicy)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
#pragma warning disable CA5359 // A self-signed test server: only the cipher negotiation is under test.
            using var ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) => true);
#pragma warning restore CA5359
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "anjal-rc7-test",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CipherSuitesPolicy = clientPolicy,
            }).ConfigureAwait(false);
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
}
