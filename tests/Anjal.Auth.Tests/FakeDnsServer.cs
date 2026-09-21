using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Auth.Tests;

/// <summary>
/// A tiny UDP DNS server answering TXT queries from a fixed table, so the
/// SPF and DKIM paths that fetch records can be exercised without the
/// network. Unknown names get NXDOMAIN.
/// </summary>
internal sealed class FakeDnsServer : System.IDisposable
{
    private readonly UdpClient udp;
    private readonly System.Collections.Generic.Dictionary<string, string> txt;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task loop;

    private readonly UdpClient? spoofer;

    public FakeDnsServer(System.Collections.Generic.Dictionary<string, string> txtRecords, bool answerFromAnotherPort = false)
    {
        if (answerFromAnotherPort)
        {
            this.spoofer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        }
        this.txt = new System.Collections.Generic.Dictionary<string, string>(txtRecords, System.StringComparer.OrdinalIgnoreCase);
        this.udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        this.EndPoint = (IPEndPoint)this.udp.Client.LocalEndPoint!;
        this.loop = System.Threading.Tasks.Task.Run(this.ServeAsync);
    }

    public IPEndPoint EndPoint { get; }

    public void Dispose()
    {
        this.cts.Cancel();
        this.udp.Dispose();
        this.spoofer?.Dispose();
        try { this.loop.Wait(1000); } catch (System.AggregateException) { }
        this.cts.Dispose();
    }

    private async System.Threading.Tasks.Task ServeAsync()
    {
        while (!this.cts.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try
            {
                req = await this.udp.ReceiveAsync(this.cts.Token);
            }
            catch (System.Exception)
            {
                return;
            }
            byte[] reply = this.Answer(req.Buffer);
            try
            {
                await (this.spoofer ?? this.udp).SendAsync(reply, req.RemoteEndPoint, this.cts.Token);
            }
            catch (System.Exception)
            {
                return;
            }
        }
    }

    private byte[] Answer(byte[] q)
    {
        // Question name starts at byte 12.
        int p = 12;
        var labels = new System.Collections.Generic.List<string>();
        while (q[p] != 0)
        {
            int len = q[p];
            labels.Add(Encoding.ASCII.GetString(q, p + 1, len));
            p += len + 1;
        }
        int questionEnd = p + 5; // zero byte + qtype(2) + qclass(2)
        string name = string.Join('.', labels);
        bool found = this.txt.TryGetValue(name, out string? value);

        var ms = new System.IO.MemoryStream();
        ms.Write(q, 0, 2);                                  // id
        ms.WriteByte(0x81);                                 // QR, RD
        ms.WriteByte(found ? (byte)0x80 : (byte)0x83);      // RA, NOERROR / NXDOMAIN
        ms.Write(new byte[] { 0, 1, 0, found ? (byte)1 : (byte)0, 0, 0, 0, 0 });
        ms.Write(q, 12, questionEnd - 12);
        if (found)
        {
            byte[] data = Encoding.ASCII.GetBytes(value!);
            var rdata = new System.IO.MemoryStream();
            for (int i = 0; i < data.Length; i += 255)
            {
                int n = System.Math.Min(255, data.Length - i);
                rdata.WriteByte((byte)n);
                rdata.Write(data, i, n);
            }
            byte[] r = rdata.ToArray();
            ms.Write(new byte[] { 0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, (byte)(r.Length >> 8), (byte)r.Length });
            ms.Write(r);
        }
        return ms.ToArray();
    }
}
