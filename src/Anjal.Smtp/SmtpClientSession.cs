using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp;

/// <summary>
/// Low-level SMTP client that connects to a single host:port and plays the
/// client side of an RFC 5321 transaction. Designed to be driven by a higher
/// level sender (<see cref="DirectMailSender"/>, <see cref="RelayMailSender"/>)
/// that decides which host to talk to.
/// </summary>
public sealed class SmtpClientSession : System.IDisposable
{
    private readonly TcpClient client;
    private System.IO.Stream stream;
    private System.IO.StreamReader reader;
    private System.IO.StreamWriter writer;
    private bool isTls;
    private bool disposed;

    private SmtpClientSession(TcpClient client)
    {
        this.client = client;
        this.stream = client.GetStream();
        this.reader = new System.IO.StreamReader(this.stream, Encoding.ASCII);
        this.writer = new System.IO.StreamWriter(this.stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
    }

    /// <summary>True after a successful STARTTLS handshake.</summary>
    public bool IsTls => this.isTls;

    /// <summary>
    /// Connect to <paramref name="host"/>:<paramref name="port"/> and read the
    /// greeting line. Throws on connection failure or non-220 greeting.
    /// </summary>
    /// <param name="host">Target hostname or IP.</param>
    /// <param name="port">Target TCP port (typically 25 or 587).</param>
    /// <param name="connectTimeout">Connect timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A connected session ready for EHLO.</returns>
    public static async System.Threading.Tasks.Task<SmtpClientSession> ConnectAsync(
        string host,
        int port,
        System.TimeSpan connectTimeout,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(host);
        var client = new TcpClient();
        try
        {
            using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(connectTimeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (System.Exception)
        {
            client.Dispose();
            throw;
        }

        var session = new SmtpClientSession(client);
        SmtpReply greeting = await session.ReadReplyAsync(ct).ConfigureAwait(false);
        if (greeting.Code != 220)
        {
            session.Dispose();
            throw new SmtpProtocolException($"Expected 220 greeting, got {greeting.Code}: {greeting.Text}");
        }
        return session;
    }

    /// <summary>
    /// Send <c>EHLO &lt;hostname&gt;</c> and read the multi-line reply.
    /// </summary>
    /// <param name="clientHostname">The hostname this client claims as.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<SmtpReply> EhloAsync(string clientHostname, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(clientHostname);
        await this.WriteLineAsync($"EHLO {clientHostname}", ct).ConfigureAwait(false);
        return await this.ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue <c>STARTTLS</c> and, on a <c>220</c> reply, upgrade the
    /// underlying stream to TLS. The caller MUST re-issue <c>EHLO</c>
    /// after this returns successfully, per RFC 3207.
    /// </summary>
    /// <param name="targetHostname">The hostname expected on the server certificate.</param>
    /// <param name="validateCertificate">When true, server certificate is validated
    /// against the system trust store. When false, any certificate is accepted -
    /// only set false for testing against self-signed certs.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The 220 reply on success. If the server replies with anything
    /// other than 2xx, this returns the reply without performing the upgrade.</returns>
    public async System.Threading.Tasks.Task<SmtpReply> StartTlsAsync(
        string targetHostname,
        bool validateCertificate,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(targetHostname);
        if (this.isTls)
        {
            throw new System.InvalidOperationException("STARTTLS already negotiated on this session.");
        }

        await this.WriteLineAsync("STARTTLS", ct).ConfigureAwait(false);
        SmtpReply reply = await this.ReadReplyAsync(ct).ConfigureAwait(false);
        if (reply.Code != 220)
        {
            return reply;
        }

        // RFC 3207: after the 220, immediately negotiate TLS. We dispose the
        // text reader/writer because they have ASCII-encoded internal buffers
        // pinned to the plaintext stream; we replace them with new ones bound
        // to the SslStream after handshake.
#pragma warning disable CA5359 // Accept-any-cert is deliberate when validateCertificate=false; caller opts in for testing.
        var ssl = new System.Net.Security.SslStream(
            this.stream,
            leaveInnerStreamOpen: false,
            validateCertificate
                ? null
                : (sender, certificate, chain, errors) => true);
#pragma warning restore CA5359
        try
        {
            await ssl.AuthenticateAsClientAsync(
                targetHostname,
                clientCertificates: null,
                enabledSslProtocols: System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                checkCertificateRevocation: false).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            try { ssl.Dispose(); } catch (System.Exception) { /* swallow */ }
            throw new SmtpProtocolException($"TLS handshake to {targetHostname} failed: {ex.Message}", ex);
        }

        this.stream = ssl;
        this.reader = new System.IO.StreamReader(this.stream, Encoding.ASCII);
        this.writer = new System.IO.StreamWriter(this.stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        this.isTls = true;
        return reply;
    }

    /// <summary>
    /// Inspect a multi-line EHLO response to see whether the server advertises
    /// the <c>STARTTLS</c> capability.
    /// </summary>
    /// <param name="ehloReply">The reply returned from <see cref="EhloAsync"/>.</param>
    /// <returns>True if the EHLO advertised STARTTLS.</returns>
    public static bool EhloSupportsStartTls(SmtpReply ehloReply)
    {
        System.ArgumentNullException.ThrowIfNull(ehloReply);
        // The reply text is multi-line joined by '\n'. Each line is a capability.
        foreach (string line in ehloReply.Text.Split('\n'))
        {
            if (string.Equals(line.Trim(), "STARTTLS", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Send <c>MAIL FROM:&lt;addr&gt;</c>.
    /// </summary>
    /// <param name="from">Sender address (no angle brackets).</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<SmtpReply> MailFromAsync(string from, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(from);
        await this.WriteLineAsync($"MAIL FROM:<{from}>", ct).ConfigureAwait(false);
        return await this.ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send <c>RCPT TO:&lt;addr&gt;</c>.
    /// </summary>
    /// <param name="to">Recipient address (no angle brackets).</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<SmtpReply> RcptToAsync(string to, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(to);
        await this.WriteLineAsync($"RCPT TO:<{to}>", ct).ConfigureAwait(false);
        return await this.ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send DATA, the message body with dot-stuffing applied, and the
    /// terminator. Returns the final reply.
    /// </summary>
    /// <param name="rawBytes">The RFC 5322 message bytes to send.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<SmtpReply> DataAsync(byte[] rawBytes, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rawBytes);

        await this.WriteLineAsync("DATA", ct).ConfigureAwait(false);
        SmtpReply dataReply = await this.ReadReplyAsync(ct).ConfigureAwait(false);
        if (dataReply.Code != 354)
        {
            return dataReply;
        }

        // Dot-stuff: any line starting with "." gets an extra "." prepended.
        byte[] stuffed = DotStuff(rawBytes);
        await this.stream.WriteAsync(stuffed, ct).ConfigureAwait(false);

        // Make sure the message ends with CRLF before the dot terminator.
        if (stuffed.Length < 2 || stuffed[stuffed.Length - 2] != (byte)'\r' || stuffed[stuffed.Length - 1] != (byte)'\n')
        {
            byte[] crlf = new byte[] { (byte)'\r', (byte)'\n' };
            await this.stream.WriteAsync(crlf, ct).ConfigureAwait(false);
        }

        await this.WriteLineAsync(".", ct).ConfigureAwait(false);
        return await this.ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send QUIT and read the 221 reply. Errors are swallowed - the caller
    /// always continues to dispose.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task QuitAsync(System.Threading.CancellationToken ct = default)
    {
        try
        {
            await this.WriteLineAsync("QUIT", ct).ConfigureAwait(false);
            await this.ReadReplyAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // best-effort QUIT - we always close regardless.
        catch (System.Exception)
        {
            // Ignore - connection is going away anyway.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Apply dot-stuffing per RFC 5321 section 4.5.2. Exposed as static for
    /// test use.
    /// </summary>
    /// <param name="data">Raw bytes.</param>
    /// <returns>Bytes with each line that starts with '.' prefixed by an extra '.'.</returns>
    public static byte[] DotStuff(byte[] data)
    {
        System.ArgumentNullException.ThrowIfNull(data);
        using var ms = new System.IO.MemoryStream(data.Length + 8);
        bool atLineStart = true;
        foreach (byte b in data)
        {
            if (atLineStart && b == (byte)'.')
            {
                ms.WriteByte((byte)'.');
            }
            ms.WriteByte(b);
            atLineStart = b == (byte)'\n';
        }
        return ms.ToArray();
    }

    private async System.Threading.Tasks.Task WriteLineAsync(string line, System.Threading.CancellationToken ct)
    {
        await this.writer.WriteAsync(line + "\r\n").ConfigureAwait(false);
        await this.writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<SmtpReply> ReadReplyAsync(System.Threading.CancellationToken ct)
    {
        var sb = new StringBuilder();
        int code = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string line = await this.reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new SmtpProtocolException("Connection closed before reply was complete.");
            if (line.Length < 4)
            {
                throw new SmtpProtocolException($"Malformed SMTP reply line: '{line}'.");
            }
            if (!int.TryParse(line.AsSpan(0, 3), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedCode))
            {
                throw new SmtpProtocolException($"Reply line does not start with three digits: '{line}'.");
            }
            code = parsedCode;
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(line.AsSpan(4));
            if (line[3] == ' ')
            {
                break;
            }
            if (line[3] != '-')
            {
                throw new SmtpProtocolException($"Reply line continuation marker is neither '-' nor ' ': '{line}'.");
            }
        }
        return new SmtpReply { Code = code, Text = sb.ToString() };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        try
        {
            this.writer.Dispose();
        }
#pragma warning disable CA1031
        catch (System.Exception) { }
#pragma warning restore CA1031
        try
        {
            this.reader.Dispose();
        }
#pragma warning disable CA1031
        catch (System.Exception) { }
#pragma warning restore CA1031
        try
        {
            this.stream.Dispose();
        }
#pragma warning disable CA1031
        catch (System.Exception) { }
#pragma warning restore CA1031
        this.client.Dispose();
    }
}

/// <summary>
/// An SMTP reply: a 3-digit numeric code and a text message.
/// </summary>
public sealed class SmtpReply
{
    /// <summary>The 3-digit reply code.</summary>
    public int Code { get; init; }

    /// <summary>The text portion, with multi-line replies joined by newlines.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Thrown when the remote server speaks SMTP improperly.
/// </summary>
public sealed class SmtpProtocolException : System.Exception
{
    /// <summary>Construct with a message.</summary>
    /// <param name="message">Description of the error.</param>
    public SmtpProtocolException(string message) : base(message) { }

    /// <summary>Construct with a message and inner exception.</summary>
    /// <param name="message">Description of the error.</param>
    /// <param name="inner">The wrapped exception.</param>
    public SmtpProtocolException(string message, System.Exception inner) : base(message, inner) { }

    /// <summary>Default constructor.</summary>
    public SmtpProtocolException() { }
}
