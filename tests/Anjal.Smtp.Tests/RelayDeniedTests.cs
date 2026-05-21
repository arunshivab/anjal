using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>
/// Tests for the MTA listener's open-relay guard: when a local-domain
/// resolver is configured, RCPT TO for non-local destinations must be
/// refused with 550 5.7.1 Relaying denied.
/// </summary>
public class RelayDeniedTests
{
    [Fact]
    public async System.Threading.Tasks.Task MtaPort_RcptForNonLocalDomain_Returns550()
    {
        using var fixture = await StartMtaWithLocalDomainsAsync("hospital-a.test").ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("MAIL FROM:<sender@other.test>").ConfigureAwait(false);
        string? mailReply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", mailReply!, System.StringComparison.Ordinal);

        await conn.WriteLineAsync("RCPT TO:<patient@gmail.com>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("550", reply!, System.StringComparison.Ordinal);
        Assert.Contains("Relaying denied", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task MtaPort_RcptForLocalDomain_Accepted()
    {
        using var fixture = await StartMtaWithLocalDomainsAsync("hospital-a.test").ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("MAIL FROM:<sender@gmail.com>").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);

        await conn.WriteLineAsync("RCPT TO:<patient@hospital-a.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task MtaPort_RcptForLocalDomain_CaseInsensitive()
    {
        using var fixture = await StartMtaWithLocalDomainsAsync("hospital-a.test").ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("MAIL FROM:<sender@gmail.com>").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);

        await conn.WriteLineAsync("RCPT TO:<patient@HOSPITAL-A.TEST>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task MtaPort_NoLocalDomainsResolver_AcceptsAll()
    {
        // Without a resolver, MTA accepts all RCPTs (legacy behavior).
        using var fixture = await StartMtaWithoutLocalDomainsAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("MAIL FROM:<sender@anywhere.test>").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);

        await conn.WriteLineAsync("RCPT TO:<patient@anywhere-else.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", reply!, System.StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static async System.Threading.Tasks.Task<Fixture> StartMtaWithLocalDomainsAsync(params string[] domains)
    {
        var resolver = new TestLocalDomainResolver(domains);
        var sink = new NoopSink();
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.local",
            Role = SmtpServerRole.Mta,
        };
        var cts = new System.Threading.CancellationTokenSource();
        var server = new SmtpServer(options, sink,
            authenticator: null, enforceReject: false,
            smtpAuthenticator: null, localDomains: resolver);
        var task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
        return new Fixture(server, task, cts);
    }

    private static async System.Threading.Tasks.Task<Fixture> StartMtaWithoutLocalDomainsAsync()
    {
        var sink = new NoopSink();
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.local",
            Role = SmtpServerRole.Mta,
        };
        var cts = new System.Threading.CancellationTokenSource();
        var server = new SmtpServer(options, sink);
        var task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
        return new Fixture(server, task, cts);
    }

    private static async System.Threading.Tasks.Task<SocketConn> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        return new SocketConn(client);
    }

    private static async System.Threading.Tasks.Task DrainEhloAsync(SocketConn conn)
    {
        string? line;
        do
        {
            line = await conn.ReadLineAsync().ConfigureAwait(false);
        } while (line is not null && line.StartsWith("250-", System.StringComparison.Ordinal));
    }

    private sealed class TestLocalDomainResolver : ILocalDomainResolver
    {
        private readonly System.Collections.Generic.HashSet<string> set;

        public TestLocalDomainResolver(string[] domains)
        {
            this.set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string d in domains) this.set.Add(d);
        }

        public System.Threading.Tasks.Task<bool> IsLocalAsync(string domain, System.Threading.CancellationToken ct = default)
        {
            return System.Threading.Tasks.Task.FromResult(this.set.Contains(domain));
        }
    }

    private sealed class Fixture : System.IDisposable
    {
        private readonly SmtpServer server;
        private readonly System.Threading.Tasks.Task task;
        private readonly System.Threading.CancellationTokenSource cts;

        public int Port { get; }

        public Fixture(SmtpServer s, System.Threading.Tasks.Task t, System.Threading.CancellationTokenSource c)
        {
            this.server = s;
            this.task = t;
            this.cts = c;
            this.Port = s.BoundPort;
        }

        public void Dispose()
        {
            this.cts.Cancel();
            try { this.task.Wait(System.TimeSpan.FromSeconds(2)); }
            catch (System.AggregateException) { }
            catch (System.OperationCanceledException) { }
            this.server.Dispose();
            this.cts.Dispose();
        }
    }

    private sealed class SocketConn : System.IDisposable
    {
        private readonly TcpClient client;
        private readonly System.IO.StreamReader reader;
        private readonly System.IO.StreamWriter writer;

        public SocketConn(TcpClient client)
        {
            this.client = client;
            var stream = client.GetStream();
            this.reader = new System.IO.StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            this.writer = new System.IO.StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };
        }

        public System.Threading.Tasks.Task<string?> ReadLineAsync() => this.reader.ReadLineAsync();
        public System.Threading.Tasks.Task WriteLineAsync(string s) => this.writer.WriteLineAsync(s);

        public void Dispose()
        {
            try { this.writer.Dispose(); } catch (System.IO.IOException) { }
            try { this.reader.Dispose(); } catch (System.IO.IOException) { }
            try { this.client.Dispose(); } catch (System.IO.IOException) { }
        }
    }

    private sealed class NoopSink : IMessageSink
    {
        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult
            {
                Outcome = DeliveryOutcome.Accepted,
                ReplyText = "ok",
            });
        }
    }
}
