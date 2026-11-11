using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>rc.28 (DES-20 §3): one IMAP connection, from the greeting to LOGOUT.</summary>
internal sealed partial class ImapSession
{
    internal const string Capabilities = "IMAP4rev1 AUTH=PLAIN LITERAL+ IDLE UIDPLUS MOVE SPECIAL-USE NAMESPACE ID UNSELECT CHILDREN";

    private const int MaxLine = 64 * 1024;

    // Before signing in a command is a few words: a literal of more than this is refused, and a connection
    // that says nothing for a minute is closed.
    private const int MaxLiteralBeforeSignIn = 8 * 1024;
    private static readonly TimeSpan WaitBeforeSignIn = TimeSpan.FromSeconds(60);

    private readonly ImapServer server;
    private readonly Stream stream;
    private readonly IImapBackend backend;
    private readonly ImapServerOptions options;
    private readonly string remote;
    private readonly bool secure;
    private readonly byte[] buffer = new byte[16 * 1024];
    private readonly Dictionary<Guid, long> sizes = new();
    private int bufferStart;
    private int bufferEnd;
    private IImapAccount? account;
    private ImapView? view;
    private List<ImapMessage> messages = new();
    private bool readOnly;
    private (Guid Id, byte[] Raw, ImapMime Mime)? lastRead;

    internal ImapSession(ImapServer server, Stream stream, IImapBackend backend, ImapServerOptions options, string remote, bool secure)
    {
        this.server = server;
        this.stream = stream;
        this.backend = backend;
        this.options = options;
        this.remote = remote;
        this.secure = secure;
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        await this.WriteAsync("* OK [CAPABILITY " + Capabilities + "] " + this.options.HostName + " Anjal IMAP ready\r\n", ct).ConfigureAwait(false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? command;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(this.account is null ? WaitBeforeSignIn : this.options.IdleTimeout);
                    try
                    {
                        command = await this.ReadCommandAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await this.WriteAsync(this.account is null ? "* BYE No sign-in; closing\r\n" : "* BYE No command for 30 minutes; signing out\r\n", ct).ConfigureAwait(false);
                        return;
                    }
                }
                if (command is null)
                {
                    return;
                }
                if (!await this.HandleAsync(command, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            if (this.account is not null)
            {
                this.server.Leave(this.account.Address);
                this.account.Dispose();
            }
        }
    }

    // One command: its line, and any literals it carries (sent straight after "+" for a synchronising one).
    private async Task<byte[]?> ReadCommandAsync(CancellationToken ct)
    {
        var all = new List<byte>();
        long allowed = MaxLine;
        long most = this.account is null ? MaxLiteralBeforeSignIn : this.options.MaxAppendBytes;
        while (true)
        {
            byte[]? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }
            all.AddRange(line);
            all.Add(13);
            all.Add(10);
            if (all.Count > MaxLine + most)
            {
                throw new IOException("Command too long.");
            }
            if (Literal(line) is not (long size, bool plus))
            {
                return all.ToArray();
            }
            allowed += size;
            if (size > most || allowed > most + MaxLine)
            {
                if (!plus)
                {
                    await this.WriteAsync(TagOf(all) + " NO [TOOBIG] The message is larger than Anjal accepts\r\n", ct).ConfigureAwait(false);
                    return Array.Empty<byte>();
                }
                throw new IOException("Literal too large.");
            }
            if (!plus)
            {
                await this.WriteAsync("+ Ready\r\n", ct).ConfigureAwait(false);
            }
            byte[] data = await this.ReadExactAsync((int)size, ct).ConfigureAwait(false);
            all.AddRange(data);
        }
    }

    private static (long Size, bool Plus)? Literal(byte[] line)
    {
        int n = line.Length;
        if (n < 3 || line[n - 1] != (byte)'}')
        {
            return null;
        }
        int open = Array.LastIndexOf(line, (byte)'{');
        if (open < 0)
        {
            return null;
        }
        string inside = Encoding.ASCII.GetString(line, open + 1, n - open - 2);
        bool plus = inside.EndsWith('+');
        return long.TryParse(plus ? inside[..^1] : inside, NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? (size, plus) : null;
    }

    private static string TagOf(List<byte> command)
    {
        int space = command.IndexOf((byte)' ');
        return space > 0 ? Encoding.ASCII.GetString(command.Take(space).ToArray()) : "*";
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (this.bufferStart == this.bufferEnd)
            {
                this.bufferStart = 0;
                this.bufferEnd = await this.stream.ReadAsync(this.buffer, ct).ConfigureAwait(false);
                if (this.bufferEnd == 0)
                {
                    return null;
                }
            }
            byte b = this.buffer[this.bufferStart++];
            if (b == 10)
            {
                if (line.Count > 0 && line[^1] == 13)
                {
                    line.RemoveAt(line.Count - 1);
                }
                return line.ToArray();
            }
            line.Add(b);
            if (line.Count > MaxLine)
            {
                throw new IOException("Line too long.");
            }
        }
    }

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken ct)
    {
        byte[] data = new byte[size];
        int got = 0;
        while (got < size)
        {
            if (this.bufferStart < this.bufferEnd)
            {
                int take = Math.Min(size - got, this.bufferEnd - this.bufferStart);
                Array.Copy(this.buffer, this.bufferStart, data, got, take);
                this.bufferStart += take;
                got += take;
                continue;
            }
            int n = await this.stream.ReadAsync(data.AsMemory(got, size - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("Connection closed inside a literal.");
            }
            got += n;
        }
        return data;
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await this.stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await this.stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(IReadOnlyList<object> parts, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        foreach (object p in parts)
        {
            byte[] bytes = p as byte[] ?? Encoding.UTF8.GetBytes((string)p);
            ms.Write(bytes);
        }
        await this.stream.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
        await this.stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
