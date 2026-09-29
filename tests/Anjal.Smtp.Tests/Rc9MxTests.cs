using System.Net;
using System.Net.Sockets;

namespace Anjal.Smtp.Tests;

/// <summary>
/// v1.0.0-rc.9 (ANJAL-TST-12): DEF-067 implicit MX (RFC 5321 5.1) and DEF-071
/// null MX (RFC 7505), against a fake DNS server and a real SMTP server.
/// </summary>
public sealed class Rc9MxTests : System.IDisposable
{
    private readonly SmtpServer smtp;
    private readonly Counting sink = new();

    public Rc9MxTests()
    {
        this.smtp = new SmtpServer(new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "dest.test" }, this.sink);
        _ = this.smtp.StartAsync(System.Threading.CancellationToken.None);
    }

    public void Dispose() => this.smtp.Dispose();

    private sealed class Counting : IMessageSink
    {
        public int Delivered { get; private set; }

        public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
        {
            this.Delivered++;
            return Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
        }
    }

    /// <summary>A DNS server on the loopback answering every MX query with the given records and response code.</summary>
    private sealed class FakeDns : System.IDisposable
    {
        private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly (int Pref, string Exchange)[] records;
        private readonly byte rcode;
        private readonly CancellationTokenSource cts = new();

        public FakeDns(byte rcode, params (int Pref, string Exchange)[] records)
        {
            this.records = records;
            this.rcode = rcode;
            _ = Task.Run(this.ServeAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)this.udp.Client.LocalEndPoint!;

        private async Task ServeAsync()
        {
            while (!this.cts.IsCancellationRequested)
            {
                UdpReceiveResult q;
                try
                {
                    q = await this.udp.ReceiveAsync(this.cts.Token);
                }
                catch (System.OperationCanceledException)
                {
                    return;
                }
                byte[] req = q.Buffer;
                int end = 12;
                while (req[end] != 0)
                {
                    end += req[end] + 1;
                }
                end += 5;   // the zero byte, QTYPE and QCLASS
                var resp = new List<byte>();
                resp.AddRange(req[..2]);
                resp.AddRange(new byte[] { 0x81, (byte)(0x80 | this.rcode), 0, 1, 0, (byte)this.records.Length, 0, 0, 0, 0 });
                resp.AddRange(req[12..end]);
                foreach ((int pref, string exchange) in this.records)
                {
                    var rdata = new List<byte> { (byte)(pref >> 8), (byte)pref };
                    foreach (string label in exchange.Split('.', System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        rdata.Add((byte)label.Length);
                        rdata.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
                    }
                    rdata.Add(0);
                    resp.AddRange(new byte[] { 0xC0, 0x0C, 0, 15, 0, 1, 0, 0, 1, 0, (byte)(rdata.Count >> 8), (byte)rdata.Count });
                    resp.AddRange(rdata);
                }
                await this.udp.SendAsync(resp.ToArray(), q.RemoteEndPoint);
            }
        }

        public void Dispose()
        {
            this.cts.Cancel();
            this.udp.Dispose();
            this.cts.Dispose();
        }
    }

    private async Task<SendResult> SendAsync(FakeDns dns, string recipient, HostLookup lookup)
    {
        var sender = new DirectMailSender(new Anjal.Dns.DnsResolver(dns.EndPoint), new DirectSenderOptions
        {
            ClientHostName = "mail.anjal.test",
            Port = this.smtp.BoundPort,
            HostLookup = (_, _) => Task.FromResult(lookup),
        });
        return await sender.SendAsync(new OutboundDelivery
        {
            EnvelopeFrom = "arun@anjal.test",
            EnvelopeTo = new[] { recipient },
            RawBytes = System.Text.Encoding.ASCII.GetBytes("From: arun@anjal.test\r\nSubject: t\r\n\r\nbody\r\n"),
        });
    }

    [Fact]
    public async Task NullMx_FailsAtOnce_With556_AndNoConnectionIsMade()
    {
        using var dns = new FakeDns(0, (0, "."));
        SendResult r = await this.SendAsync(dns, "someone@nomail.test", HostLookup.Found);
        Assert.Equal(SendOutcome.PermanentFailure, r.Outcome);
        Assert.Equal(556, r.ReplyCode);
        Assert.Contains("5.1.10", r.Message, System.StringComparison.Ordinal);
        Assert.Equal(0, this.sink.Delivered);
    }

    [Fact]
    public async Task NoMx_ButAnAddress_IsDeliveredToTheDomainItself()
    {
        using var dns = new FakeDns(0);
        SendResult r = await this.SendAsync(dns, "someone@localhost", HostLookup.Found);
        Assert.Equal(SendOutcome.Sent, r.Outcome);
        Assert.Equal("localhost", r.RemoteHost);
        Assert.Equal(1, this.sink.Delivered);
    }

    [Theory]
    [InlineData(0)]   // no MX records
    [InlineData(3)]   // NXDOMAIN
    public async Task NoMx_AndNoAddress_FailsPermanently_AndNoConnectionIsMade(byte rcode)
    {
        using var dns = new FakeDns(rcode);
        SendResult r = await this.SendAsync(dns, "someone@nothing.test", HostLookup.NotFound);
        Assert.Equal(SendOutcome.PermanentFailure, r.Outcome);
        Assert.Contains("5.1.2", r.Message, System.StringComparison.Ordinal);
        Assert.Equal(0, this.sink.Delivered);
    }

    [Fact]
    public async Task NoMx_AndATemporaryLookupFailure_IsRetriedLater()
    {
        using var dns = new FakeDns(0);
        SendResult r = await this.SendAsync(dns, "someone@flaky.test", HostLookup.TryAgain);
        Assert.Equal(SendOutcome.TransientFailure, r.Outcome);
    }

    [Fact]
    public async Task AnOrdinaryMx_IsUsed_AsBefore()
    {
        using var dns = new FakeDns(0, (10, "localhost"));
        SendResult r = await this.SendAsync(dns, "someone@withmx.test", HostLookup.NotFound);
        Assert.Equal(SendOutcome.Sent, r.Outcome);
        Assert.Equal(1, this.sink.Delivered);
    }

    [Fact]
    public async Task ANullMxBesideRealOnes_IsIgnored_AndTheRealOneUsed()
    {
        using var dns = new FakeDns(0, (0, "."), (10, "localhost"));
        SendResult r = await this.SendAsync(dns, "someone@mixed.test", HostLookup.NotFound);
        Assert.Equal(SendOutcome.Sent, r.Outcome);
    }
}
