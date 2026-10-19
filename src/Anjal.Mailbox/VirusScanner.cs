using System.Net.Sockets;
using System.Text;

namespace Anjal.Mailbox;

/// <summary>What a scan found.</summary>
public enum ScanVerdict
{
    /// <summary>Nothing found.</summary>
    Clean = 0,

    /// <summary>A virus, named in the result.</summary>
    Infected = 1,

    /// <summary>The scanner did not answer; nothing is known about the file.</summary>
    Unavailable = 2,
}

/// <summary>The result of scanning one file.</summary>
/// <param name="Verdict">What was found.</param>
/// <param name="Name">The virus's name, or why the scanner was unavailable; empty when clean.</param>
public sealed record ScanResult(ScanVerdict Verdict, string Name);

/// <summary>A virus scanner (rc.22, item 39): ClamAV on this server, or a stand-in in tests.</summary>
public interface IVirusScanner
{
    /// <summary>Scan one file's bytes.</summary>
    /// <param name="data">The file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The verdict.</returns>
    Task<ScanResult> ScanAsync(byte[] data, CancellationToken ct = default);

    /// <summary>The scanner's version line, or null when it does not answer.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>For example "ClamAV 1.4.2/27450/Sat Oct 18 05:10:00 2026".</returns>
    Task<string?> VersionAsync(CancellationToken ct = default);
}

/// <summary>
/// ClamAV's clamd, spoken to over its socket with the INSTREAM command (rc.22, item 39; decisions
/// of 2 Oct 2026). Nothing leaves the server: the file's bytes go to clamd on this machine and
/// back comes "OK" or the virus's name. Set with ANJAL_CLAMAV: "host:port" (clamd's TCP socket,
/// usually 127.0.0.1:3310) or the path of its Unix socket (/run/clamav/clamd.ctl on Debian).
/// </summary>
public sealed class ClamdScanner : IVirusScanner
{
    /// <summary>The setting that names the scanner.</summary>
    public const string Setting = "ANJAL_CLAMAV";

    /// <summary>The longest wait for clamd to answer one file.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string endpoint;

    /// <summary>Make a scanner for an endpoint.</summary>
    /// <param name="endpoint">"host:port" or a Unix socket path.</param>
    public ClamdScanner(string endpoint)
    {
        System.ArgumentNullException.ThrowIfNull(endpoint);
        this.endpoint = endpoint.Trim();
    }

    /// <summary>The scanner the setting names, or null when the setting is empty (scanning off).</summary>
    /// <param name="setting">The value of <see cref="Setting"/>.</param>
    /// <returns>The scanner, or null.</returns>
    public static ClamdScanner? FromSetting(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? null : new ClamdScanner(setting);

    /// <inheritdoc/>
    public async Task<ScanResult> ScanAsync(byte[] data, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(data);
        try
        {
            string answer = await this.TalkAsync("zINSTREAM\0", data, ct).ConfigureAwait(false);
            // "stream: OK" or "stream: Eicar-Signature FOUND" or "INSTREAM size limit exceeded. ERROR".
            string line = answer.Trim('\0', '\r', '\n', ' ');
            if (line.EndsWith(" FOUND", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                string name = line[(colon + 1)..^6].Trim();
                return new ScanResult(ScanVerdict.Infected, name.Length > 0 ? name : "virus");
            }
            if (line.EndsWith(" OK", StringComparison.Ordinal) || line.EndsWith(":OK", StringComparison.Ordinal))
            {
                return new ScanResult(ScanVerdict.Clean, string.Empty);
            }
            return new ScanResult(ScanVerdict.Unavailable, line.Length > 0 ? line : "no answer");
        }
#pragma warning disable CA1031 // A scanner that cannot be reached is "unavailable", whatever the cause; the mail is then held.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ScanResult(ScanVerdict.Unavailable, ex.Message);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc/>
    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            string answer = await this.TalkAsync("zVERSION\0", null, ct).ConfigureAwait(false);
            string line = answer.Trim('\0', '\r', '\n', ' ');
            // rc.22.2 (found with a real clamd 1.5): EnableVersionCommand is off by default, so the
            // daemon answers "COMMAND UNAVAILABLE" while scanning perfectly well; PING then says it is up.
            if (line.Length == 0 || line.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                string pong = (await this.TalkAsync("zPING\0", null, ct).ConfigureAwait(false)).Trim('\0', '\r', '\n', ' ');
                return pong == "PONG" ? "ClamAV (version not disclosed; EnableVersionCommand is off)" : null;
            }
            return line;
        }
#pragma warning disable CA1031 // Not answering is the answer.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<string> TalkAsync(string command, byte[]? stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using Socket socket = this.endpoint.Contains(':', StringComparison.Ordinal)
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (this.endpoint.Contains(':', StringComparison.Ordinal))
        {
            int colon = this.endpoint.LastIndexOf(':');
            string host = this.endpoint[..colon];
            int port = int.Parse(this.endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        else
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(this.endpoint), timeout.Token).ConfigureAwait(false);
        }
        using var net = new NetworkStream(socket, ownsSocket: false);
        await net.WriteAsync(Encoding.ASCII.GetBytes(command), timeout.Token).ConfigureAwait(false);
        if (stream is not null)
        {
            const int chunk = 64 * 1024;
            var length = new byte[4];
            for (int at = 0; at < stream.Length; at += chunk)
            {
                int n = Math.Min(chunk, stream.Length - at);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, n);
                await net.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await net.WriteAsync(stream.AsMemory(at, n), timeout.Token).ConfigureAwait(false);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, 0);
            await net.WriteAsync(length, timeout.Token).ConfigureAwait(false);
        }
        await net.FlushAsync(timeout.Token).ConfigureAwait(false);
        var answer = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            int read = await net.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            answer.Write(buffer, 0, read);
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(answer.ToArray());
    }
}

/// <summary>What was decided about a message's files before delivery (rc.22, item 39).</summary>
public enum FileVerdict
{
    /// <summary>No file, or every file clean: deliver as it is.</summary>
    Clean = 0,

    /// <summary>A program or script: the whole message is refused (550), the sender told.</summary>
    Refused = 1,

    /// <summary>A disk image: delivered to Junk.</summary>
    Junk = 2,

    /// <summary>A virus: the file is removed and the message delivered without it.</summary>
    Removed = 3,

    /// <summary>The scanner is down: the message is held until it can be checked.</summary>
    Held = 4,
}

/// <summary>The result of judging a message's files.</summary>
/// <param name="Verdict">What to do.</param>
/// <param name="Files">The files concerned, by name.</param>
/// <param name="Detail">The virus name, or why the scanner was unavailable.</param>
/// <param name="RawBytes">The message to deliver (rebuilt when a file was removed), or null to deliver the original.</param>
/// <param name="Scanned">How many files were scanned.</param>
public sealed record FileJudgement(FileVerdict Verdict, IReadOnlyList<string> Files, string Detail, byte[]? RawBytes, int Scanned);

/// <summary>
/// The rules for files on incoming mail, as accepted 2 Oct 2026 (rc.22, item 39): programs and
/// scripts are refused; disk images go to Junk; every other file is scanned by ClamAV, a virus is
/// removed and the recipient told; if the scanner is down, the mail is held in quarantine and the
/// operator told at once and every 30 minutes. The rules are the same for every organisation.
/// </summary>
public static class AttachmentPolicy
{
    /// <summary>The folder held mail waits in; never shown as a folder of the person's own.</summary>
    public const string QuarantineFolder = "Quarantine";

    /// <summary>The header on a message a file was removed from: the file names and the virus.</summary>
    public const string RemovedHeader = "X-Anjal-Virus-Removed";

    /// <summary>The header on a held message: why.</summary>
    public const string HeldHeader = "X-Anjal-Held";

    private static readonly string[] Programs =
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".scr", ".pif", ".cpl", ".dll", ".sys", ".drv",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".reg", ".jar", ".app",
        ".sh", ".bash", ".py", ".pl", ".rb", ".php", ".lnk", ".inf", ".vb", ".ws", ".msc", ".scf", ".apk", ".deb", ".rpm",
    };

    private static readonly string[] DiskImages = { ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".dmg", ".bin", ".toast" };

    /// <summary>Whether a file name is a program or script, by its ending.</summary>
    /// <param name="fileName">The name.</param>
    /// <returns>True when refused.</returns>
    public static bool IsProgram(string fileName) => EndsWithOneOf(fileName, Programs);

    /// <summary>Whether a file name is a disk image, by its ending.</summary>
    /// <param name="fileName">The name.</param>
    /// <returns>True when sent to Junk.</returns>
    public static bool IsDiskImage(string fileName) => EndsWithOneOf(fileName, DiskImages);

    /// <summary>The file name of a part: its Content-Disposition filename, else its Content-Type name, else empty.</summary>
    /// <param name="part">The part.</param>
    /// <returns>The name, decoded.</returns>
    public static string FileNameOf(Anjal.Mime.MimePart part)
    {
        System.ArgumentNullException.ThrowIfNull(part);
        string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
        string name = ParameterOf(disposition, "filename");
        if (name.Length == 0 && part.ContentType.Parameters.TryGetValue("name", out string? n))
        {
            name = n;
        }
        return Anjal.Mime.EncodedWordDecoder.Decode(name).Trim().Trim('"');
    }

    /// <summary>
    /// Judge a message's files: the name rules first, then the scanner (when there is one) on
    /// every file that is not plain text. A message without files is clean at once.
    /// </summary>
    /// <param name="parsed">The message; null when it could not be parsed (then clean: nothing to inspect).</param>
    /// <param name="scanner">The scanner, or null when scanning is off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The judgement.</returns>
    public static async Task<FileJudgement> JudgeAsync(Anjal.Mime.MimeMessage? parsed, IVirusScanner? scanner, CancellationToken ct = default)
    {
        if (parsed is null)
        {
            return new FileJudgement(FileVerdict.Clean, Array.Empty<string>(), string.Empty, null, 0);
        }
        var files = new List<(Anjal.Mime.MimePart Part, Anjal.Mime.MimeMultipart? Parent, string Name)>();
        Collect(parsed.Body, null, files);
        if (files.Count == 0)
        {
            return new FileJudgement(FileVerdict.Clean, Array.Empty<string>(), string.Empty, null, 0);
        }
        List<string> programs = files.Where(f => IsProgram(f.Name)).Select(f => f.Name).ToList();
        if (programs.Count > 0)
        {
            return new FileJudgement(FileVerdict.Refused, programs, "Programs and scripts are not accepted", null, 0);
        }
        List<string> images = files.Where(f => IsDiskImage(f.Name)).Select(f => f.Name).ToList();
        if (scanner is null)
        {
            return images.Count > 0
                ? new FileJudgement(FileVerdict.Junk, images, "Disk image", null, 0)
                : new FileJudgement(FileVerdict.Clean, Array.Empty<string>(), string.Empty, null, 0);
        }
        var removed = new List<string>();
        string virus = string.Empty;
        int scanned = 0;
        foreach ((Anjal.Mime.MimePart part, Anjal.Mime.MimeMultipart? parent, string name) in files)
        {
            ScanResult result = await scanner.ScanAsync(part.Body, ct).ConfigureAwait(false);
            if (result.Verdict == ScanVerdict.Unavailable)
            {
                return new FileJudgement(FileVerdict.Held, files.Select(f => f.Name).ToList(), result.Name, null, scanned);
            }
            scanned++;
            if (result.Verdict == ScanVerdict.Infected)
            {
                removed.Add(name);
                virus = result.Name;
                Anjal.Mime.MimePart notice = RemovedNotice(name, result.Name);
                if (parent is null)
                {
                    // The whole message was the file: the notice becomes the body, under the same headers.
                    foreach (Anjal.Mime.MimeHeader h in part.Headers)
                    {
                        if (!h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                        {
                            notice.Headers.Add(h.Name, h.Value);
                        }
                    }
                    parsed.Body = notice;
                }
                else
                {
                    parent.Parts[parent.Parts.IndexOf(part)] = notice;
                }
            }
        }
        if (removed.Count > 0)
        {
            parsed.Headers.Set(RemovedHeader, string.Join(", ", removed) + "; " + virus);
            string subject = parsed.Headers.Get("Subject") ?? string.Empty;
            parsed.Headers.Set("Subject", subject + " (attachment removed: " + string.Join(", ", removed) + ", " + virus + ")");
            return new FileJudgement(FileVerdict.Removed, removed, virus, Anjal.Mime.MimeBuilder.Build(parsed), scanned);
        }
        return images.Count > 0
            ? new FileJudgement(FileVerdict.Junk, images, "Disk image", null, scanned)
            : new FileJudgement(FileVerdict.Clean, Array.Empty<string>(), string.Empty, null, scanned);
    }

    private static Anjal.Mime.MimePart RemovedNotice(string fileName, string virus)
    {
        var notice = new Anjal.Mime.MimePart();
        notice.Headers.Set("Content-Type", "text/plain; charset=us-ascii");
        notice.Headers.Set("Content-Transfer-Encoding", "7bit");
        notice.SetBodyAsText("Attachment removed by Anjal: " + Ascii(fileName) + " (" + Ascii(virus) + "). The file carried a virus and was not delivered.\r\n", Encoding.ASCII);
        return notice;
    }

    private static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
        }
        return sb.ToString();
    }

    private static void Collect(Anjal.Mime.MimeEntity entity, Anjal.Mime.MimeMultipart? parent, List<(Anjal.Mime.MimePart, Anjal.Mime.MimeMultipart?, string)> files)
    {
        if (entity is Anjal.Mime.MimeMultipart multi)
        {
            foreach (Anjal.Mime.MimeEntity child in multi.Parts.ToList())
            {
                Collect(child, multi, files);
            }
            return;
        }
        if (entity is Anjal.Mime.MimePart part)
        {
            string name = FileNameOf(part);
            string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
            bool attachment = disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) || name.Length > 0
                || !part.ContentType.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
            if (attachment && part.Body.Length > 0)
            {
                files.Add((part, parent, name.Length > 0 ? name : "attachment"));
            }
        }
    }

    private static bool EndsWithOneOf(string fileName, string[] endings)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }
        string lower = fileName.Trim().ToLowerInvariant();
        foreach (string ending in endings)
        {
            if (lower.EndsWith(ending, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string ParameterOf(string header, string name)
    {
        foreach (string piece in header.Split(';'))
        {
            string p = piece.Trim();
            int eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && p[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return p[(eq + 1)..].Trim().Trim('"');
            }
            if (eq > 0 && p[..eq].Trim().Equals(name + "*", StringComparison.OrdinalIgnoreCase))
            {
                string v = p[(eq + 1)..].Trim();
                int q = v.LastIndexOf('\'');
                return q >= 0 ? Uri.UnescapeDataString(v[(q + 1)..]) : v;
            }
        }
        return string.Empty;
    }
}
