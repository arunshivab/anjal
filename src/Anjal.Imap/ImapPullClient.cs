using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Imap;

/// <summary>A message copied from another server.</summary>
/// <param name="Uid">Its UID there.</param>
/// <param name="Received">Its INTERNALDATE there, when given.</param>
/// <param name="Seen">Read there.</param>
/// <param name="Flagged">Flagged there.</param>
/// <param name="Raw">Its bytes.</param>
public sealed record PulledMessage(long Uid, DateTimeOffset? Received, bool Seen, bool Flagged, byte[] Raw);

/// <summary>
/// rc.28 (DES-20 §5): the little IMAP client that copies a person's old mail from their previous mail
/// server - sign in, list the folders, read each in UID order, a batch at a time. It only reads: it never
/// changes, moves or deletes anything there (every FETCH is BODY.PEEK, every folder EXAMINEd).
/// </summary>
public sealed partial class ImapPullClient : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly Stream stream;
    private readonly byte[] buffer = new byte[64 * 1024];
    private int start;
    private int end;
    private int tagNumber;

    private ImapPullClient(TcpClient tcp, Stream stream)
    {
        this.tcp = tcp;
        this.stream = stream;
    }

    /// <summary>The largest message copied; a larger one is skipped.</summary>
    public const int MaxMessage = 50 * 1024 * 1024;

    // One answer (every line and literal of one command) and how long it may take.
    private const long MaxAnswer = MaxMessage + (16L * 1024 * 1024);
    private static readonly TimeSpan CommandTime = TimeSpan.FromMinutes(3);

    /// <summary>Connect and read the greeting.</summary>
    /// <param name="host">The server.</param>
    /// <param name="port">Its port (993).</param>
    /// <param name="tls">TLS from the first byte (always, but in tests).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The client.</returns>
    public static async Task<ImapPullClient> ConnectAsync(string host, int port, bool tls, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        var tcp = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            await tcp.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);
            Stream s = tcp.GetStream();
            if (tls)
            {
                var ssl = new SslStream(s, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, connect.Token).ConfigureAwait(false);
                s = ssl;
            }
            var client = new ImapPullClient(tcp, s);
            string greeting = await client.ReadLineTextAsync(ct).ConfigureAwait(false);
            if (!greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) && !greeting.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new IOException("The server did not greet as an IMAP server.");
            }
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Sign in.</summary>
    /// <param name="user">The account.</param>
    /// <param name="password">Its password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null when signed in; the server's words otherwise.</returns>
    public async Task<string?> LoginAsync(string user, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        (bool ok, List<(string Line, byte[]? Literal)> lines) = await this.CommandAsync("LOGIN " + Quote(user) + " " + Quote(password), ct).ConfigureAwait(false);
        return ok ? null : lines.Count > 0 ? lines[^1].Line : "The server refused the sign-in.";
    }

    /// <summary>The account's folders (selectable ones only).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The folder names, as the server writes them (modified UTF-7 decoded).</returns>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        (bool ok, List<(string Line, byte[]? Literal)> lines) = await this.CommandAsync("LIST \"\" \"*\"", ct).ConfigureAwait(false);
        var names = new List<string>();
        if (!ok)
        {
            return names;
        }
        foreach ((string line, byte[]? literal) in lines)
        {
            Match m = ListLine().Match(line);
            if (!m.Success || m.Groups[1].Value.Contains("\\Noselect", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value.Contains("\\NonExistent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string name = literal is not null ? Encoding.UTF8.GetString(literal) : m.Groups[2].Value.Trim().Trim('"').Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            names.Add(ImapText.DecodeFolder(name));
        }
        return names;
    }

    /// <summary>Open a folder for reading only, and list its UIDs above <paramref name="after"/>.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="after">The last UID already copied (0 for none).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The UIDs, oldest first; null when the folder cannot be opened.</returns>
    public async Task<IReadOnlyList<long>?> UidsAsync(string folder, long after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folder);
        (bool ok, _) = await this.CommandAsync("EXAMINE " + Quote(ImapText.EncodeFolder(folder)), ct).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }
        (bool found, List<(string Line, byte[]? Literal)> lines) = await this.CommandAsync("UID SEARCH UID " + (after + 1).ToString(CultureInfo.InvariantCulture) + ":*", ct).ConfigureAwait(false);
        var uids = new List<long>();
        if (!found)
        {
            return uids;
        }
        foreach ((string line, _) in lines.Where(l => l.Line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (string n in line[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out long uid) && uid > after)
                {
                    uids.Add(uid);
                }
            }
        }
        uids.Sort();
        return uids;
    }

    /// <summary>Read some messages of the open folder, never marking them read there.</summary>
    /// <param name="uids">Their UIDs.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The messages read.</returns>
    public async Task<IReadOnlyList<PulledMessage>> FetchAsync(IReadOnlyCollection<long> uids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uids);
        var pulled = new List<PulledMessage>();
        if (uids.Count == 0)
        {
            return pulled;
        }
        // Their sizes first: a message larger than Anjal copies is passed over (returned empty, so it is
        // counted as skipped), and the rest are read in groups that keep each answer within its limit.
        (bool sized, List<(string Line, byte[]? Literal)> sizeLines) = await this.CommandAsync("UID FETCH " + ImapText.WriteSet(uids) + " (UID RFC822.SIZE)", ct).ConfigureAwait(false);
        var groups = new List<List<long>> { new() };
        long groupBytes = 0;
        foreach ((string line, byte[]? _) in sized ? sizeLines : new List<(string Line, byte[]? Literal)>())
        {
            Match uid = UidOf().Match(line);
            Match size = SizeOf().Match(line);
            if (!line.StartsWith('*') || !uid.Success)
            {
                continue;
            }
            long u = long.Parse(uid.Groups[1].Value, CultureInfo.InvariantCulture);
            long bytes = size.Success && long.TryParse(size.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long b) ? b : 0;
            if (bytes > MaxMessage)
            {
                pulled.Add(new PulledMessage(u, null, false, false, Array.Empty<byte>()));
                continue;
            }
            if (groups[^1].Count > 0 && groupBytes + bytes > MaxMessage)
            {
                groups.Add(new List<long>());
                groupBytes = 0;
            }
            groups[^1].Add(u);
            groupBytes += bytes;
        }
        foreach (List<long> group in groups.Where(g => g.Count > 0))
        {
            await this.FetchGroupAsync(group, pulled, ct).ConfigureAwait(false);
        }
        return pulled;
    }

    private async Task FetchGroupAsync(List<long> uids, List<PulledMessage> pulled, CancellationToken ct)
    {
        (bool ok, List<(string Line, byte[]? Literal)> lines) = await this.CommandAsync("UID FETCH " + ImapText.WriteSet(uids) + " (UID FLAGS INTERNALDATE RFC822.SIZE BODY.PEEK[])", ct).ConfigureAwait(false);
        if (!ok)
        {
            return;
        }
        foreach ((string line, byte[]? literal) in lines)
        {
            if (literal is null || !line.StartsWith('*'))
            {
                continue;
            }
            Match uid = UidOf().Match(line);
            Match date = DateOf().Match(line);
            Match flags = FlagsOf().Match(line);
            if (!uid.Success)
            {
                continue;
            }
            string f = flags.Success ? flags.Groups[1].Value : string.Empty;
            pulled.Add(new PulledMessage(
                long.Parse(uid.Groups[1].Value, CultureInfo.InvariantCulture),
                date.Success ? ImapText.ParseDate(date.Groups[1].Value) : null,
                f.Contains("\\Seen", StringComparison.OrdinalIgnoreCase),
                f.Contains("\\Flagged", StringComparison.OrdinalIgnoreCase),
                literal));
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await this.CommandAsync("LOGOUT", bye.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Saying goodbye is a courtesy; the connection closes either way.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
        await this.stream.DisposeAsync().ConfigureAwait(false);
        this.tcp.Dispose();
    }

    // A command and every line until its tagged answer; a literal at the end of a line is read whole
    // and kept with that line (the line's rest, after the literal, is appended to it).
    private async Task<(bool Ok, List<(string Line, byte[]? Literal)> Lines)> CommandAsync(string command, CancellationToken ct)
    {
        string tag = "p" + (++this.tagNumber).ToString(CultureInfo.InvariantCulture);
        // The other server is chosen by the person, so it is not trusted: each answer has a time and a size
        // it must keep within, or the import stops with a clear error and the next job runs.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CommandTime);
        ct = limit.Token;
        await this.stream.WriteAsync(Encoding.UTF8.GetBytes(tag + " " + command + "\r\n"), ct).ConfigureAwait(false);
        await this.stream.FlushAsync(ct).ConfigureAwait(false);
        var lines = new List<(string, byte[]?)>();
        long total = 0;
        while (true)
        {
            string line = await this.ReadLineTextAsync(ct).ConfigureAwait(false);
            total += line.Length;
            byte[]? literal = null;
            Match lit = LiteralAtEnd().Match(line);
            while (lit.Success)
            {
                long size = long.Parse(lit.Groups[1].Value, CultureInfo.InvariantCulture);
                total += size;
                if (total > MaxAnswer)
                {
                    throw new IOException("The other server's answer is larger than Anjal reads.");
                }
                byte[] data = await this.ReadExactAsync(size, ct).ConfigureAwait(false);
                literal ??= data;
                line = line[..lit.Index] + " " + await this.ReadLineTextAsync(ct).ConfigureAwait(false);
                lit = LiteralAtEnd().Match(line);
            }
            if (total > MaxAnswer || lines.Count > 200_000)
            {
                throw new IOException("The other server's answer is larger than Anjal reads.");
            }
            if (line.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                lines.Add((line, null));
                return (line[(tag.Length + 1)..].StartsWith("OK", StringComparison.OrdinalIgnoreCase), lines);
            }
            lines.Add((line, literal));
        }
    }

    private async Task<string> ReadLineTextAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (this.start == this.end)
            {
                this.start = 0;
                this.end = await this.stream.ReadAsync(this.buffer, ct).ConfigureAwait(false);
                if (this.end == 0)
                {
                    throw new IOException("The server closed the connection.");
                }
            }
            byte b = this.buffer[this.start++];
            if (b == 10)
            {
                return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            }
            line.Add(b);
            if (line.Count > 1024 * 1024)
            {
                throw new IOException("The server sent a line too long.");
            }
        }
    }

    private async Task<byte[]> ReadExactAsync(long size, CancellationToken ct)
    {
        if (size > MaxMessage + (1024 * 1024))
        {
            throw new IOException("A message is larger than Anjal copies.");
        }
        byte[] data = new byte[size];
        int got = 0;
        while (got < size)
        {
            if (this.start < this.end)
            {
                int take = (int)Math.Min(size - got, this.end - this.start);
                Array.Copy(this.buffer, this.start, data, got, take);
                this.start += take;
                got += take;
                continue;
            }
            int n = await this.stream.ReadAsync(data.AsMemory(got, (int)size - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("The server closed the connection.");
            }
            got += n;
        }
        return data;
    }

    private static string Quote(string text) =>
        text.Any(c => c > 126 || c is '\r' or '\n') ? "{" + Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture) + "+}\r\n" + text
        : "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"\{(\d+)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralAtEnd();

    [GeneratedRegex(@"^\* LIST \(([^)]*)\) (?:""[^""]*""|NIL) (.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"\bUID (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex UidOf();

    [GeneratedRegex(@"\bRFC822\.SIZE (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeOf();

    [GeneratedRegex(@"INTERNALDATE ""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex DateOf();

    [GeneratedRegex(@"FLAGS \(([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex FlagsOf();
}
