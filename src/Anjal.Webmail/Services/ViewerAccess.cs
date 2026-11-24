using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Anjal.Viewing;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.29 (DES-22 §4, §6.2): the webmail's way to the separate viewer process. On a server it speaks to the
/// <c>anjal-viewer</c> service's socket (<c>ANJAL_VIEWER_SOCKET</c>). On a developer's machine, with no socket set,
/// it starts the viewer itself the first time a file is shown (from <c>ANJAL_VIEWER_PATH</c>, or the build beside
/// this one), and stops it with the webmail. <c>ANJAL_VIEWER=off</c> turns viewing off: files are Download only.
/// <para>
/// It also keeps, per signed-in session and only in memory (D-362): the pictures and text of viewed files for 10
/// minutes or 64 MB, whichever first, and a password the person typed for a file, for the same 10 minutes. Both
/// are dropped at sign-out. Nothing is written to disk.
/// </para>
/// </summary>
public sealed class ViewerAccess : IDisposable
{
    /// <summary>How long a viewed file's pictures and text, and its password, are kept (D-362).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>The most a session keeps of viewed files (D-362).</summary>
    public const long KeepBytes = 64L * 1024 * 1024;

    private readonly string? socket;
    private readonly string? localProgram;
    private readonly bool off;
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
    private readonly ConcurrentDictionary<string, SessionKeep> keeps = new(StringComparer.Ordinal);
    private readonly object startLock = new();
    private Process? local;
    private string? localSocket;
    private ViewerClient? client;

    /// <summary>Creates the access.</summary>
    /// <param name="socket">The viewer service's socket, or null.</param>
    /// <param name="localProgram">The viewer program to start on this machine when there is no socket, or null.</param>
    /// <param name="off">True when viewing is switched off.</param>
    public ViewerAccess(string? socket, string? localProgram, bool off = false)
    {
        this.socket = string.IsNullOrWhiteSpace(socket) ? null : socket;
        this.localProgram = localProgram;
        this.off = off;
        if (this.socket is not null)
        {
            this.client = new ViewerClient(this.socket);
        }
    }

    /// <summary>
    /// rc.29 (D-372); rc.30: whether DICOM files are offered in the viewer - a file's preview, a ZIP's study grouping
    /// and the series view, on SIAARA v0.1.0-rc.1. Off unless <c>ANJAL_VIEWER_DICOM=on</c>: a test server only until
    /// SIAARA v0.1.0. The tests switch it on.
    /// </summary>
    public bool DicomOn { get; internal set; }

    /// <summary>
    /// The PDF editor's held-back parts (the library's security advisory, D-405 and D-406, 17 Nov 2026): redaction,
    /// and changing the pages of a file with hidden layers. Off in rc.29 - not an operator's setting - and on in the
    /// release that moves to chuvadi-pdf 3.35.1; the tests turn them on to check what they guard.
    /// </summary>
    public EditSwitches Switches { get; internal set; } = EditSwitches.Shipped;

    /// <summary>True when files can be viewed (a viewer is set or can be started).</summary>
    public bool Enabled => !this.off && (this.socket is not null || this.localProgram is not null);

    /// <summary>The access as the environment sets it up.</summary>
    /// <returns>The access.</returns>
    public static ViewerAccess FromEnvironment()
    {
        bool off = string.Equals(Environment.GetEnvironmentVariable("ANJAL_VIEWER"), "off", StringComparison.OrdinalIgnoreCase);
        string? socket = Environment.GetEnvironmentVariable("ANJAL_VIEWER_SOCKET");
        string? program = socket is null && !off ? FindLocalProgram() : null;
        return new ViewerAccess(socket, program, off)
        {
            DicomOn = string.Equals(Environment.GetEnvironmentVariable("ANJAL_VIEWER_DICOM"), "on", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>One line for the start-up log: where files are viewed.</summary>
    /// <returns>The line.</returns>
    public string Describe() =>
        this.off ? "Viewer: off (ANJAL_VIEWER=off); attachments are Download only."
        : this.socket is not null ? $"Viewer: the anjal-viewer service at {this.socket}."
        : this.localProgram is not null ? $"Viewer: started on this machine when a file is first shown ({this.localProgram})."
        : "Viewer: not found (set ANJAL_VIEWER_SOCKET or ANJAL_VIEWER_PATH); attachments are Download only.";

    /// <summary>An opaque key for a person, so the viewer can share itself fairly without knowing who they are.</summary>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <returns>The key.</returns>
    public string WhoKey(Guid mailboxId)
    {
        byte[] hash = HMACSHA256.HashData(this.salt, mailboxId.ToByteArray());
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// Asks the viewer to do a job on a file, from the session's keep when it was done in the last 10 minutes. The
    /// file is read only when the viewer has to be asked.
    /// </summary>
    /// <param name="session">The signed-in session.</param>
    /// <param name="fileKey">The file (mailbox, mail, attachment): part of what the answer is kept under.</param>
    /// <param name="request">The request.</param>
    /// <param name="file">Reads the file's bytes (decrypted with the person's keys, for this request only).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public async Task<ViewResult> AskAsync(string session, string fileKey, ViewRequest request, Func<Task<byte[]?>> file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fileKey);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(file);
        if (!this.Enabled)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        string key = KeyOf(fileKey, request);
        SessionKeep keep = this.keeps.GetOrAdd(session, _ => new SessionKeep());
        if (keep.Find(key) is ViewResult kept)
        {
            return kept;
        }
        ViewerClient? viewer = this.Client();
        if (viewer is null)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        byte[]? bytes = await file().ConfigureAwait(false);
        if (bytes is null)
        {
            return ViewResult.Of(ViewStatus.Failed);
        }
        if (bytes.Length > ViewFrames.MaxFileBytes)
        {
            return ViewResult.Of(ViewStatus.TooLarge);
        }
        ViewResult result = await viewer.AskAsync(request, bytes, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            keep.Add(key, result);
        }
        return result;
    }

    /// <summary>
    /// rc.29 part 2 (DES-22 §7): asks the viewer once, with several files and nothing kept - the editor's arrange and
    /// save, whose answers belong to the request that asked.
    /// </summary>
    /// <param name="request">The job.</param>
    /// <param name="files">The files; the first is the attachment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public async Task<ViewResult> AskOnceAsync(ViewRequest request, IReadOnlyList<byte[]> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        if (!this.Enabled || this.Client() is not ViewerClient viewer)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        return await viewer.AskAsync(request, files.Select(f => (ReadOnlyMemory<byte>)f).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>rc.29 part 2: keeps a file the editor made (the pages as they will be) in the session's memory, like a viewed file's pictures (D-362).</summary>
    /// <param name="session">The session.</param>
    /// <param name="key">What it is kept under.</param>
    /// <param name="file">The file.</param>
    public void KeepFile(string session, string key, byte[] file)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(file);
        this.keeps.GetOrAdd(session, _ => new SessionKeep()).Add("file\u001F" + key, new ViewResult(new ViewAnswer(), new[] { file }));
    }

    /// <summary>rc.29 part 2: a file kept by <see cref="KeepFile"/>, or null when it has gone (10 minutes, or the session's 64 MB).</summary>
    /// <param name="session">The session.</param>
    /// <param name="key">What it was kept under.</param>
    /// <returns>The file, or null.</returns>
    public byte[]? KeptFile(string session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(key);
        return this.keeps.TryGetValue(session, out SessionKeep? keep) && keep.Find("file\u001F" + key) is ViewResult r && r.Files.Count > 0 ? r.Files[0] : null;
    }

    /// <summary>Keeps a password the person typed for a file, for 10 minutes, in memory.</summary>
    /// <param name="session">The session.</param>
    /// <param name="fileKey">The file (message, attachment, entry).</param>
    /// <param name="password">The password.</param>
    public void RememberPassword(string session, string fileKey, string password)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fileKey);
        ArgumentNullException.ThrowIfNull(password);
        this.keeps.GetOrAdd(session, _ => new SessionKeep()).SetPassword(fileKey, password);
    }

    /// <summary>The password kept for a file, if any.</summary>
    /// <param name="session">The session.</param>
    /// <param name="fileKey">The file.</param>
    /// <returns>The password, or null.</returns>
    public string? PasswordFor(string session, string fileKey) =>
        this.keeps.TryGetValue(session, out SessionKeep? keep) ? keep.Password(fileKey) : null;

    /// <summary>Drops everything kept for a signed-in session (at sign-out), for every mailbox it opened.</summary>
    /// <param name="sessionId">The session's id.</param>
    public void ForgetSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        foreach (string key in this.keeps.Keys.Where(k => k.StartsWith(sessionId + "/", StringComparison.Ordinal)).ToList())
        {
            this.keeps.TryRemove(key, out _);
        }
    }

    /// <summary>How many sessions have something kept (for a test).</summary>
    public int SessionsKept => this.keeps.Count;

    /// <summary>A copy of a request with a password, a person's key and a language set.</summary>
    /// <param name="r">The request.</param>
    /// <param name="password">The password, or null.</param>
    /// <param name="who">The person's key.</param>
    /// <param name="culture">The language and region.</param>
    /// <returns>The copy.</returns>
    public static ViewRequest With(ViewRequest r, string? password, string? who, string? culture)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ViewRequest
        {
            Kind = r.Kind,
            Name = r.Name,
            ContentType = r.ContentType,
            Password = password,
            Page = r.Page,
            Width = r.Width,
            Query = r.Query,
            Sheet = r.Sheet,
            Entry = r.Entry,
            Culture = culture,
            Who = who,
            ArchivePassword = r.ArchivePassword,
            InnerArchivePassword = r.InnerArchivePassword,
            Plan = r.Plan,
            Switches = r.Switches,
        };
    }

    /// <summary>AC-20: a copy of a request with the passwords of the archive that holds the file, and of the archive inside it.</summary>
    /// <param name="r">The request.</param>
    /// <param name="archive">The attachment's (the archive's) password, or null.</param>
    /// <param name="inner">The inner archive's password, or null.</param>
    /// <returns>The copy.</returns>
    public static ViewRequest WithArchive(ViewRequest r, string? archive, string? inner)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ViewRequest
        {
            Kind = r.Kind,
            Name = r.Name,
            ContentType = r.ContentType,
            Password = r.Password,
            Page = r.Page,
            Width = r.Width,
            Query = r.Query,
            Sheet = r.Sheet,
            Entry = r.Entry,
            Culture = r.Culture,
            Who = r.Who,
            ArchivePassword = archive,
            InnerArchivePassword = inner,
            Plan = r.Plan,
            Switches = r.Switches,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (this.startLock)
        {
            if (this.local is not null)
            {
                try
                {
                    if (!this.local.HasExited)
                    {
                        this.local.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                this.local.Dispose();
                this.local = null;
            }
            if (this.localSocket is not null)
            {
                try
                {
                    File.Delete(this.localSocket);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    // The request's identity in the keep: everything that changes the answer, never the password itself (a file
    // opened with a password is kept under its own key, so the same file without one is asked again).
    private static string KeyOf(string fileKey, ViewRequest r) =>
        string.Join('\u001F', fileKey, r.Kind, r.Name ?? string.Empty, r.Entry ?? string.Empty, r.Page, r.Width, r.Sheet, r.Query ?? string.Empty, r.Culture ?? string.Empty, string.IsNullOrEmpty(r.Password) ? "0" : "1", string.IsNullOrEmpty(r.ArchivePassword) ? "0" : "1", string.IsNullOrEmpty(r.InnerArchivePassword) ? "0" : "1");

    private ViewerClient? Client()
    {
        if (this.client is not null)
        {
            return this.client;
        }
        if (this.localProgram is null)
        {
            return null;
        }
        lock (this.startLock)
        {
            if (this.client is null)
            {
                this.StartLocal();
            }
            return this.client;
        }
    }

    // A developer's machine: the viewer started beside the webmail, on a socket of its own, stopping with it.
    private void StartLocal()
    {
        string path = Path.Combine(Path.GetTempPath(), "anjal-viewer-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)) + ".sock");
        bool dll = this.localProgram!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(dll ? (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? DotnetHost()) : this.localProgram)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (dll)
        {
            info.ArgumentList.Add(this.localProgram);
        }
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(path);
        info.ArgumentList.Add("--parent");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            this.local = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (this.local is null)
        {
            return;
        }
        this.local.OutputDataReceived += (_, _) => { };
        this.local.ErrorDataReceived += (_, _) => { };
        this.local.BeginOutputReadLine();
        this.local.BeginErrorReadLine();
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path) && wait.Elapsed < TimeSpan.FromSeconds(30) && !this.local.HasExited)
        {
            Thread.Sleep(50);
        }
        if (File.Exists(path))
        {
            this.localSocket = path;
            this.client = new ViewerClient(path);
        }
    }

    private static string DotnetHost()
    {
        string? process = Environment.ProcessPath;
        return process is not null && Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? process : "dotnet";
    }

    // The viewer built beside this webmail: ANJAL_VIEWER_PATH, else src/Anjal.Viewer/bin/<configuration>/net10.0
    // found by walking up from this program's folder (a developer's checkout and the test runs).
    private static string? FindLocalProgram()
    {
        string? set = Environment.GetEnvironmentVariable("ANJAL_VIEWER_PATH");
        if (!string.IsNullOrWhiteSpace(set))
        {
            return File.Exists(set) ? set : null;
        }
        string here = AppContext.BaseDirectory;
        string configuration = here.Replace('\\', '/').Contains("/Debug/", StringComparison.Ordinal) ? "Debug" : "Release";
        for (DirectoryInfo? dir = new(here); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Anjal.Viewer", "bin", configuration, "net10.0", "Anjal.Viewer.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    // One session's keep: answers by key, least recently used dropped first, each for 10 minutes; passwords by file.
    private sealed class SessionKeep
    {
        private readonly LinkedList<(string Key, ViewResult Result, long Bytes, DateTimeOffset Until)> order = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, ViewResult Result, long Bytes, DateTimeOffset Until)>> byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Password, DateTimeOffset Until)> passwords = new(StringComparer.Ordinal);
        private long bytes;

        public ViewResult? Find(string key)
        {
            lock (this)
            {
                if (!this.byKey.TryGetValue(key, out var node))
                {
                    return null;
                }
                if (node.Value.Until < DateTimeOffset.UtcNow)
                {
                    this.Remove(node);
                    return null;
                }
                this.order.Remove(node);
                this.order.AddFirst(node);
                return node.Value.Result;
            }
        }

        public void Add(string key, ViewResult result)
        {
            long size = result.Files.Sum(f => (long)f.Length) + (result.Answer.Json?.Length ?? 0) * 2L;
            if (size > KeepBytes)
            {
                return;
            }
            lock (this)
            {
                if (this.byKey.TryGetValue(key, out var old))
                {
                    this.Remove(old);
                }
                while (this.bytes + size > KeepBytes && this.order.Last is { } last)
                {
                    this.Remove(last);
                }
                this.byKey[key] = this.order.AddFirst((key, result, size, DateTimeOffset.UtcNow + KeepFor));
                this.bytes += size;
            }
        }

        public void SetPassword(string fileKey, string password)
        {
            lock (this)
            {
                this.passwords[fileKey] = (password, DateTimeOffset.UtcNow + KeepFor);
            }
        }

        public string? Password(string fileKey)
        {
            lock (this)
            {
                if (this.passwords.TryGetValue(fileKey, out var p) && p.Until > DateTimeOffset.UtcNow)
                {
                    return p.Password;
                }
                this.passwords.Remove(fileKey);
                return null;
            }
        }

        private void Remove(LinkedListNode<(string Key, ViewResult Result, long Bytes, DateTimeOffset Until)> node)
        {
            this.order.Remove(node);
            this.byKey.Remove(node.Value.Key);
            this.bytes -= node.Value.Bytes;
        }
    }
}
