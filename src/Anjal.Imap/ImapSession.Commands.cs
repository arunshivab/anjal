using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>rc.28 (DES-20 §3): the commands.</summary>
internal sealed partial class ImapSession
{
    private const string Flags = "(\\Seen \\Flagged \\Answered \\Deleted \\Draft)";

    // False ends the session.
    private async Task<bool> HandleAsync(byte[] command, CancellationToken ct)
    {
        if (command.Length == 0)
        {
            return true;
        }
        List<ImapToken> t;
        try
        {
            t = ImapToken.Read(command);
        }
        catch (FormatException)
        {
            int space = Array.IndexOf(command, (byte)' ');
            string tagged = space > 0 && space <= 64 ? Encoding.ASCII.GetString(command, 0, space) : "*";
            await this.WriteAsync(tagged + " BAD That command is nested too deeply\r\n", ct).ConfigureAwait(false);
            return true;
        }
        if (t.Count < 2 || t[0].Kind != 'a' || t[1].Kind != 'a')
        {
            await this.WriteAsync((t.Count > 0 ? t[0].Text : "*") + " BAD Say a tag and a command\r\n", ct).ConfigureAwait(false);
            return true;
        }
        string tag = t[0].Text;
        string name = t[1].Text.ToUpperInvariant();
        List<ImapToken> args = t.Skip(2).ToList();
        bool uid = false;
        if (name == "UID" && args.Count > 0 && args[0].Kind == 'a')
        {
            uid = true;
            name = args[0].Text.ToUpperInvariant();
            args.RemoveAt(0);
        }
        try
        {
            switch (name)
            {
                case "CAPABILITY":
                    await this.WriteAsync("* CAPABILITY " + Capabilities + "\r\n" + tag + " OK CAPABILITY done\r\n", ct).ConfigureAwait(false);
                    return true;
                case "NOOP":
                case "CHECK":
                    await this.RefreshAsync(ct).ConfigureAwait(false);
                    await this.WriteAsync(tag + " OK " + name + " done\r\n", ct).ConfigureAwait(false);
                    return true;
                case "LOGOUT":
                    await this.WriteAsync("* BYE Anjal says goodbye\r\n" + tag + " OK LOGOUT done\r\n", ct).ConfigureAwait(false);
                    return false;
                case "ID":
                    await this.WriteAsync("* ID (\"name\" \"Anjal\" \"vendor\" \"Anjal\")\r\n" + tag + " OK ID done\r\n", ct).ConfigureAwait(false);
                    return true;
                case "STARTTLS":
                    await this.WriteAsync(tag + " BAD This port is already encrypted\r\n", ct).ConfigureAwait(false);
                    return true;
                case "LOGIN":
                    await this.LoginAsync(tag, args.Count >= 2 ? args[0].Text : string.Empty, args.Count >= 2 ? args[1].Text : string.Empty, ct).ConfigureAwait(false);
                    return true;
                case "AUTHENTICATE":
                    await this.AuthenticateAsync(tag, args, ct).ConfigureAwait(false);
                    return true;
                default:
                    break;
            }
            if (this.account is null)
            {
                await this.WriteAsync(tag + " BAD Sign in first\r\n", ct).ConfigureAwait(false);
                return true;
            }
            if (!await this.account.StillAllowedAsync(ct).ConfigureAwait(false))
            {
                await this.WriteAsync("* BYE This app password was removed, or the account was switched off\r\n", ct).ConfigureAwait(false);
                return false;
            }
            switch (name)
            {
                case "NAMESPACE":
                    await this.WriteAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL\r\n" + tag + " OK NAMESPACE done\r\n", ct).ConfigureAwait(false);
                    break;
                case "LIST":
                case "LSUB":
                    await this.ListAsync(tag, name, args, ct).ConfigureAwait(false);
                    break;
                case "SUBSCRIBE":
                case "UNSUBSCRIBE":
                    await this.WriteAsync(tag + " OK " + name + " done\r\n", ct).ConfigureAwait(false);
                    break;
                case "STATUS":
                    await this.StatusAsync(tag, args, ct).ConfigureAwait(false);
                    break;
                case "SELECT":
                case "EXAMINE":
                    await this.SelectAsync(tag, args.Count > 0 ? ImapText.DecodeFolder(args[0].Text) : string.Empty, name == "EXAMINE", ct).ConfigureAwait(false);
                    break;
                case "CREATE":
                    await this.Answer(tag, name, await this.account.CreateAsync(Folder(args), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    break;
                case "DELETE":
                    await this.Answer(tag, name, await this.account.RemoveAsync(Folder(args), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    break;
                case "RENAME":
                    await this.Answer(tag, name, await this.account.RenameAsync(Folder(args), args.Count > 1 ? ImapText.DecodeFolder(args[1].Text) : string.Empty, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    break;
                case "APPEND":
                    await this.AppendAsync(tag, args, ct).ConfigureAwait(false);
                    break;
                default:
                    await this.SelectedAsync(tag, name, uid, args, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (IOException)
        {
            throw;
        }
#pragma warning disable CA1031 // A command that fails says so; the session goes on.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            this.options.Log?.Invoke($"IMAP: {name} failed for {this.account?.Address ?? this.remote}: {ex.GetType().Name}: {ex.Message}");
            await this.WriteAsync(tag + " NO [SERVERBUG] Anjal could not do that; try again\r\n", ct).ConfigureAwait(false);
        }
#pragma warning restore CA1031
        return true;
    }

    private static string Folder(List<ImapToken> args) => args.Count > 0 ? ImapText.DecodeFolder(args[0].Text) : string.Empty;

    private Task Answer(string tag, string name, string? refusal, CancellationToken ct) =>
        this.WriteAsync(refusal is null ? tag + " OK " + name + " done\r\n" : tag + " NO " + refusal + "\r\n", ct);

    private async Task SelectedAsync(string tag, string name, bool uid, List<ImapToken> args, CancellationToken ct)
    {
        if (this.view is null && name is "FETCH" or "STORE" or "SEARCH" or "COPY" or "MOVE" or "EXPUNGE" or "CLOSE" or "UNSELECT" or "IDLE")
        {
            if (name == "IDLE")
            {
                await this.IdleAsync(tag, ct).ConfigureAwait(false);
                return;
            }
            await this.WriteAsync(tag + " BAD Select a folder first\r\n", ct).ConfigureAwait(false);
            return;
        }
        switch (name)
        {
            case "FETCH":
                await this.FetchAsync(tag, uid, args, ct).ConfigureAwait(false);
                break;
            case "STORE":
                await this.StoreAsync(tag, uid, args, ct).ConfigureAwait(false);
                break;
            case "SEARCH":
                await this.SearchAsync(tag, uid, args, ct).ConfigureAwait(false);
                break;
            case "COPY":
            case "MOVE":
                await this.CopyAsync(tag, uid, name == "MOVE", args, ct).ConfigureAwait(false);
                break;
            case "EXPUNGE":
                await this.ExpungeAsync(uid && args.Count > 0 ? ImapText.ParseSet(args[0].Text, this.MaxUid) : null, ct).ConfigureAwait(false);
                await this.WriteAsync(tag + " OK EXPUNGE done\r\n", ct).ConfigureAwait(false);
                break;
            case "CLOSE":
                if (!this.readOnly)
                {
                    await this.ExpungeAsync(null, ct, silent: true).ConfigureAwait(false);
                }
                this.Deselect();
                await this.WriteAsync(tag + " OK CLOSE done\r\n", ct).ConfigureAwait(false);
                break;
            case "UNSELECT":
                this.Deselect();
                await this.WriteAsync(tag + " OK UNSELECT done\r\n", ct).ConfigureAwait(false);
                break;
            case "IDLE":
                await this.IdleAsync(tag, ct).ConfigureAwait(false);
                break;
            default:
                await this.WriteAsync(tag + " BAD Anjal does not know " + name + "\r\n", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task LoginAsync(string tag, string user, string password, CancellationToken ct)
    {
        if (this.account is not null)
        {
            await this.WriteAsync(tag + " BAD Already signed in\r\n", ct).ConfigureAwait(false);
            return;
        }
        if (this.options.AuthFailures is { } limiter && !limiter.IsAllowed(this.remote))
        {
            await this.WriteAsync(tag + " NO [UNAVAILABLE] Too many wrong passwords from here; try again later\r\n", ct).ConfigureAwait(false);
            return;
        }
        ImapLogin login = user.Length == 0 ? new ImapLogin(null, null) : await this.backend.LoginAsync(user.Trim(), password, this.remote, ct).ConfigureAwait(false);
        if (login.Account is null)
        {
            this.options.AuthFailures?.RecordFailure(this.remote);
            await this.WriteAsync(tag + " NO [AUTHENTICATIONFAILED] " + (login.Refusal ?? "Wrong address or app password") + "\r\n", ct).ConfigureAwait(false);
            return;
        }
        if (!this.server.Enter(login.Account.Address))
        {
            login.Account.Dispose();
            await this.WriteAsync(tag + " NO [LIMIT] Too many mail app connections for this mailbox\r\n", ct).ConfigureAwait(false);
            return;
        }
        this.account = login.Account;
        await this.WriteAsync(tag + " OK [CAPABILITY " + Capabilities + "] Signed in\r\n", ct).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(string tag, List<ImapToken> args, CancellationToken ct)
    {
        if (args.Count == 0 || !args[0].Is("PLAIN"))
        {
            await this.WriteAsync(tag + " NO [CANNOT] Only PLAIN is offered\r\n", ct).ConfigureAwait(false);
            return;
        }
        string response;
        if (args.Count > 1)
        {
            response = args[1].Text;
        }
        else
        {
            await this.WriteAsync("+ \r\n", ct).ConfigureAwait(false);
            byte[]? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
            response = line is null ? "*" : Encoding.ASCII.GetString(line);
        }
        if (response == "*")
        {
            await this.WriteAsync(tag + " BAD Sign-in cancelled\r\n", ct).ConfigureAwait(false);
            return;
        }
        string[] parts;
        try
        {
            parts = Encoding.UTF8.GetString(Convert.FromBase64String(response.Trim())).Split('\0');
        }
        catch (FormatException)
        {
            await this.WriteAsync(tag + " BAD That is not base64\r\n", ct).ConfigureAwait(false);
            return;
        }
        await this.LoginAsync(tag, parts.Length == 3 ? parts[1] : string.Empty, parts.Length == 3 ? parts[2] : string.Empty, ct).ConfigureAwait(false);
    }

    private async Task ListAsync(string tag, string name, List<ImapToken> args, CancellationToken ct)
    {
        // LIST (SPECIAL-USE) "" "*" RETURN (...): the selection options and the RETURN part are allowed and ignored.
        List<ImapToken> a = args.Where(x => x.Kind != 'l').TakeWhile(x => !x.Is("RETURN")).ToList();
        string reference = a.Count > 0 ? a[0].Text : string.Empty;
        string pattern = a.Count > 1 ? a[1].Text : string.Empty;
        var sb = new StringBuilder();
        if (pattern.Length == 0)
        {
            sb.Append("* ").Append(name).Append(" (\\Noselect) \"/\" \"\"\r\n");
        }
        else
        {
            string full = ImapText.DecodeFolder(reference + pattern);
            IReadOnlyList<ImapFolder> folders = await this.account!.FoldersAsync(ct).ConfigureAwait(false);
            foreach (ImapFolder f in folders)
            {
                if (!Matches(full, f.Name))
                {
                    continue;
                }
                bool children = folders.Any(o => o.Name.StartsWith(f.Name + "/", StringComparison.Ordinal));
                string attrs = (children ? "\\HasChildren" : "\\HasNoChildren") + (f.SpecialUse.Length > 0 ? " " + f.SpecialUse : string.Empty);
                sb.Append("* ").Append(name).Append(" (").Append(attrs).Append(") \"/\" ").Append(ImapText.Quote(ImapText.EncodeFolder(f.Name))).Append("\r\n");
            }
        }
        sb.Append(tag).Append(" OK ").Append(name).Append(" done\r\n");
        await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    // "*" matches anything; "%" anything but the delimiter; INBOX in any case.
    private static bool Matches(string pattern, string name)
    {
        // Runs of wildcards are one wildcard, and the match never backtracks: a pattern cannot make it slow.
        pattern = System.Text.RegularExpressions.Regex.Replace(pattern, "[*%]{2,}", m => m.Value.Contains('*', StringComparison.Ordinal) ? "*" : "%", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        string re = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("%", "[^/]*", StringComparison.Ordinal) + "$";
        System.Text.RegularExpressions.RegexOptions o = System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking;
        return System.Text.RegularExpressions.Regex.IsMatch(name, re, o) || (string.Equals(name, "INBOX", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch("INBOX", re, o | System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private async Task StatusAsync(string tag, List<ImapToken> args, CancellationToken ct)
    {
        string folder = Folder(args);
        ImapView? v = await this.account!.OpenAsync(folder, ct).ConfigureAwait(false);
        if (v is null || args.Count < 2 || args[1].Kind != 'l')
        {
            await this.WriteAsync(tag + " NO [NONEXISTENT] There is no folder by that name\r\n", ct).ConfigureAwait(false);
            return;
        }
        var items = new List<string>();
        foreach (ImapToken item in args[1].Items)
        {
            string w = item.Text.ToUpperInvariant();
            long value = w switch
            {
                "MESSAGES" => v.Messages.Count,
                "UNSEEN" => v.Messages.Count(m => !m.Seen),
                "UIDNEXT" => v.UidNext,
                "UIDVALIDITY" => v.UidValidity,
                "RECENT" => 0,
                _ => -1,
            };
            if (value >= 0)
            {
                items.Add(w + " " + value.ToString(CultureInfo.InvariantCulture));
            }
        }
        await this.WriteAsync("* STATUS " + ImapText.Quote(ImapText.EncodeFolder(v.Folder.Name)) + " (" + string.Join(' ', items) + ")\r\n" + tag + " OK STATUS done\r\n", ct).ConfigureAwait(false);
    }

    private async Task SelectAsync(string tag, string folder, bool examine, CancellationToken ct)
    {
        this.Deselect();
        ImapView? v = await this.account!.OpenAsync(folder, ct).ConfigureAwait(false);
        if (v is null)
        {
            await this.WriteAsync(tag + " NO [NONEXISTENT] There is no folder by that name\r\n", ct).ConfigureAwait(false);
            return;
        }
        this.view = v;
        this.messages = v.Messages.ToList();
        this.readOnly = examine;
        var sb = new StringBuilder();
        sb.Append("* FLAGS ").Append(Flags).Append("\r\n");
        sb.Append("* OK [PERMANENTFLAGS ").Append(examine ? "()" : "(\\Seen \\Flagged \\Answered \\Deleted)").Append("] Flags kept\r\n");
        sb.Append("* ").Append(this.messages.Count.ToString(CultureInfo.InvariantCulture)).Append(" EXISTS\r\n* 0 RECENT\r\n");
        int firstUnseen = this.messages.FindIndex(m => !m.Seen);
        if (firstUnseen >= 0)
        {
            sb.Append("* OK [UNSEEN ").Append((firstUnseen + 1).ToString(CultureInfo.InvariantCulture)).Append("] First unread\r\n");
        }
        sb.Append("* OK [UIDVALIDITY ").Append(v.UidValidity.ToString(CultureInfo.InvariantCulture)).Append("] UIDs valid\r\n");
        sb.Append("* OK [UIDNEXT ").Append(v.UidNext.ToString(CultureInfo.InvariantCulture)).Append("] Next UID\r\n");
        sb.Append(tag).Append(examine ? " OK [READ-ONLY] EXAMINE done\r\n" : " OK [READ-WRITE] SELECT done\r\n");
        await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private void Deselect()
    {
        this.view = null;
        this.messages = new List<ImapMessage>();
        this.lastRead = null;
    }

    private long MaxUid => this.messages.Count == 0 ? 0 : this.messages[^1].Uid;

    // New mail, mail gone and flags changed elsewhere (the webmail, another app) since the last look.
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (this.view is null)
        {
            return;
        }
        ImapView? now = await this.account!.OpenAsync(this.view.Folder.Name, ct).ConfigureAwait(false);
        if (now is null)
        {
            return;
        }
        var sb = new StringBuilder();
        var stillThere = now.Messages.ToDictionary(m => m.Id);
        for (int i = this.messages.Count - 1; i >= 0; i--)
        {
            if (!stillThere.ContainsKey(this.messages[i].Id))
            {
                sb.Append("* ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" EXPUNGE\r\n");
                this.messages.RemoveAt(i);
            }
        }
        for (int i = 0; i < this.messages.Count; i++)
        {
            ImapMessage mine = this.messages[i];
            ImapMessage theirs = stillThere[mine.Id];
            if (mine.Seen != theirs.Seen || mine.Flagged != theirs.Flagged || mine.Answered != theirs.Answered)
            {
                mine.Seen = theirs.Seen;
                mine.Flagged = theirs.Flagged;
                mine.Answered = theirs.Answered;
                sb.Append("* ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" FETCH (UID ").Append(mine.Uid.ToString(CultureInfo.InvariantCulture)).Append(" FLAGS ").Append(this.FlagsOf(mine)).Append(")\r\n");
            }
        }
        var known = this.messages.Select(m => m.Id).ToHashSet();
        int before = this.messages.Count;
        this.messages.AddRange(now.Messages.Where(m => !known.Contains(m.Id)));
        if (this.messages.Count != before)
        {
            sb.Append("* ").Append(this.messages.Count.ToString(CultureInfo.InvariantCulture)).Append(" EXISTS\r\n");
        }
        this.view = now;
        if (sb.Length > 0)
        {
            await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    private async Task IdleAsync(string tag, CancellationToken ct)
    {
        await this.WriteAsync("+ idling\r\n", ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(this.options.IdleTimeout);
        Task<byte[]?> done = this.ReadLineAsync(timeout.Token);
        while (true)
        {
            Task tick = Task.Delay(this.options.IdlePoll, timeout.Token);
            Task first = await Task.WhenAny(done, tick).ConfigureAwait(false);
            if (first == done)
            {
                break;
            }
            if (timeout.IsCancellationRequested)
            {
                await this.WriteAsync("* BYE Idle for 30 minutes; signing out\r\n", ct).ConfigureAwait(false);
                throw new IOException("Idle too long.");
            }
            if (this.account is not null && !await this.account.StillAllowedAsync(ct).ConfigureAwait(false))
            {
                await this.WriteAsync("* BYE This app password was removed, or the account was switched off\r\n", ct).ConfigureAwait(false);
                throw new IOException("No longer allowed.");
            }
            await this.RefreshAsync(ct).ConfigureAwait(false);
        }
        byte[]? line;
        try
        {
            line = await done.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Idle too long.");
        }
        if (line is null)
        {
            throw new IOException("Connection closed during IDLE.");
        }
        await this.WriteAsync(string.Equals(Encoding.ASCII.GetString(line).Trim(), "DONE", StringComparison.OrdinalIgnoreCase) ? tag + " OK IDLE done\r\n" : tag + " BAD Expected DONE\r\n", ct).ConfigureAwait(false);
    }

    private string FlagsOf(ImapMessage m)
    {
        var f = new List<string>();
        if (m.Seen)
        {
            f.Add("\\Seen");
        }
        if (m.Flagged)
        {
            f.Add("\\Flagged");
        }
        if (m.Answered)
        {
            f.Add("\\Answered");
        }
        if (m.Deleted)
        {
            f.Add("\\Deleted");
        }
        if (this.view?.Folder.SpecialUse == "\\Drafts")
        {
            f.Add("\\Draft");
        }
        return "(" + string.Join(' ', f) + ")";
    }

    // The messages a set names, with their sequence numbers.
    private List<(int Seq, ImapMessage Message)> Pick(string set, bool uid)
    {
        var picked = new List<(int, ImapMessage)>();
        Func<long, bool>? test = ImapText.ParseSet(set, uid ? this.MaxUid : this.messages.Count);
        if (test is null)
        {
            return picked;
        }
        for (int i = 0; i < this.messages.Count; i++)
        {
            if (test(uid ? this.messages[i].Uid : i + 1))
            {
                picked.Add((i + 1, this.messages[i]));
            }
        }
        return picked;
    }

    private async Task<(byte[] Raw, ImapMime Mime)?> RawAsync(ImapMessage m, CancellationToken ct)
    {
        if (this.lastRead is { } last && last.Id == m.Id)
        {
            return (last.Raw, last.Mime);
        }
        byte[]? raw = await this.account!.ReadAsync(m.Id, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }
        ImapMime mime = ImapMime.Parse(raw);
        this.lastRead = (m.Id, raw, mime);
        this.sizes[m.Id] = raw.LongLength;
        return (raw, mime);
    }

    private async Task<long> SizeAsync(ImapMessage m, CancellationToken ct)
    {
        if (this.sizes.TryGetValue(m.Id, out long size))
        {
            return size;
        }
        return (await this.RawAsync(m, ct).ConfigureAwait(false))?.Raw.LongLength ?? 0;
    }

    private async Task StoreAsync(string tag, bool uid, List<ImapToken> args, CancellationToken ct)
    {
        if (this.readOnly)
        {
            await this.WriteAsync(tag + " NO [READ-ONLY] The folder was opened with EXAMINE\r\n", ct).ConfigureAwait(false);
            return;
        }
        if (args.Count < 3)
        {
            await this.WriteAsync(tag + " BAD STORE needs a set, an action and flags\r\n", ct).ConfigureAwait(false);
            return;
        }
        string action = args[1].Text.ToUpperInvariant();
        bool silent = action.EndsWith(".SILENT", StringComparison.Ordinal);
        string mode = action.Replace(".SILENT", string.Empty, StringComparison.Ordinal);
        IEnumerable<string> words = args[2].Kind == 'l' ? args[2].Items.Select(x => x.Text) : args.Skip(2).Select(x => x.Text);
        var flags = words.Select(w => w.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach ((int seq, ImapMessage m) in this.Pick(args[0].Text, uid))
        {
            bool Apply(bool current, string flag) => mode switch
            {
                "FLAGS" => flags.Contains(flag),
                "+FLAGS" => current || flags.Contains(flag),
                "-FLAGS" => current && !flags.Contains(flag),
                _ => current,
            };
            bool seen = Apply(m.Seen, "\\SEEN");
            bool flagged = Apply(m.Flagged, "\\FLAGGED");
            bool answered = Apply(m.Answered, "\\ANSWERED");
            m.Deleted = Apply(m.Deleted, "\\DELETED");
            if (seen != m.Seen || flagged != m.Flagged || answered != m.Answered)
            {
                await this.account!.SetFlagsAsync(m.Id, seen, flagged, answered, ct).ConfigureAwait(false);
                (m.Seen, m.Flagged, m.Answered) = (seen, flagged, answered);
            }
            if (!silent)
            {
                sb.Append("* ").Append(seq.ToString(CultureInfo.InvariantCulture)).Append(" FETCH (").Append(uid ? "UID " + m.Uid.ToString(CultureInfo.InvariantCulture) + " " : string.Empty).Append("FLAGS ").Append(this.FlagsOf(m)).Append(")\r\n");
            }
        }
        sb.Append(tag).Append(" OK STORE done\r\n");
        await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task CopyAsync(string tag, bool uid, bool move, List<ImapToken> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await this.WriteAsync(tag + " BAD Say which messages and where\r\n", ct).ConfigureAwait(false);
            return;
        }
        if (move && this.readOnly)
        {
            await this.WriteAsync(tag + " NO [READ-ONLY] The folder was opened with EXAMINE\r\n", ct).ConfigureAwait(false);
            return;
        }
        string target = ImapText.DecodeFolder(args[1].Text);
        if (await this.account!.OpenAsync(target, ct).ConfigureAwait(false) is null)
        {
            await this.WriteAsync(tag + " NO [TRYCREATE] There is no folder by that name\r\n", ct).ConfigureAwait(false);
            return;
        }
        List<(int Seq, ImapMessage Message)> picked = this.Pick(args[0].Text, uid);
        var copied = new List<(long From, Guid To)>();
        foreach ((int _, ImapMessage m) in picked)
        {
            if (move)
            {
                if (await this.account.MoveAsync(m.Id, target, ct).ConfigureAwait(false))
                {
                    copied.Add((m.Uid, m.Id));
                }
            }
            else if (await this.account.CopyAsync(m.Id, target, ct).ConfigureAwait(false) is Guid copy)
            {
                copied.Add((m.Uid, copy));
            }
        }
        string code = string.Empty;
        if (copied.Count > 0 && await this.account.OpenAsync(target, ct).ConfigureAwait(false) is ImapView there)
        {
            var uids = there.Messages.ToDictionary(x => x.Id, x => x.Uid);
            var pairs = copied.Where(c => uids.ContainsKey(c.To)).ToList();
            if (pairs.Count > 0)
            {
                code = "[COPYUID " + there.UidValidity.ToString(CultureInfo.InvariantCulture) + " " + string.Join(',', pairs.Select(p => p.From.ToString(CultureInfo.InvariantCulture)))
                    + " " + string.Join(',', pairs.Select(p => uids[p.To].ToString(CultureInfo.InvariantCulture))) + "] ";
            }
        }
        if (!move)
        {
            await this.WriteAsync(tag + " OK " + code + "COPY done\r\n", ct).ConfigureAwait(false);
            return;
        }
        var sb = new StringBuilder();
        if (code.Length > 0)
        {
            sb.Append("* OK ").Append(code).Append("Moved\r\n");
        }
        var gone = copied.Select(c => c.To).ToHashSet();
        for (int i = this.messages.Count - 1; i >= 0; i--)
        {
            if (gone.Contains(this.messages[i].Id))
            {
                sb.Append("* ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" EXPUNGE\r\n");
                this.messages.RemoveAt(i);
            }
        }
        sb.Append(tag).Append(" OK MOVE done\r\n");
        await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task ExpungeAsync(Func<long, bool>? uids, CancellationToken ct, bool silent = false)
    {
        if (this.readOnly)
        {
            return;
        }
        var sb = new StringBuilder();
        for (int i = this.messages.Count - 1; i >= 0; i--)
        {
            ImapMessage m = this.messages[i];
            if (!m.Deleted || (uids is not null && !uids(m.Uid)))
            {
                continue;
            }
            if (await this.account!.DeleteAsync(m.Id, ct).ConfigureAwait(false))
            {
                sb.Append("* ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" EXPUNGE\r\n");
                this.messages.RemoveAt(i);
            }
        }
        if (!silent && sb.Length > 0)
        {
            await this.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    private async Task AppendAsync(string tag, List<ImapToken> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await this.WriteAsync(tag + " BAD APPEND needs a folder and a message\r\n", ct).ConfigureAwait(false);
            return;
        }
        string folder = ImapText.DecodeFolder(args[0].Text);
        ImapToken message = args[^1];
        bool seen = false, flagged = false, answered = false;
        DateTimeOffset? when = null;
        foreach (ImapToken a in args.Skip(1).Take(args.Count - 2))
        {
            if (a.Kind == 'l')
            {
                var f = a.Items.Select(x => x.Text.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
                (seen, flagged, answered) = (f.Contains("\\SEEN"), f.Contains("\\FLAGGED"), f.Contains("\\ANSWERED"));
            }
            else
            {
                when = ImapText.ParseDate(a.Text) ?? when;
            }
        }
        if (message.Bytes is not byte[] raw || raw.Length == 0)
        {
            await this.WriteAsync(tag + " BAD APPEND needs the message as a literal\r\n", ct).ConfigureAwait(false);
            return;
        }
        (Guid? id, string? refusal) = await this.account!.AppendAsync(folder, raw, seen, flagged, answered, when, ct).ConfigureAwait(false);
        if (id is null)
        {
            await this.WriteAsync(tag + " NO " + (refusal ?? "Not kept") + "\r\n", ct).ConfigureAwait(false);
            return;
        }
        string code = string.Empty;
        if (await this.account.OpenAsync(folder, ct).ConfigureAwait(false) is ImapView there && there.Messages.FirstOrDefault(m => m.Id == id) is ImapMessage added)
        {
            code = "[APPENDUID " + there.UidValidity.ToString(CultureInfo.InvariantCulture) + " " + added.Uid.ToString(CultureInfo.InvariantCulture) + "] ";
        }
        if (this.view is not null && string.Equals(this.view.Folder.Name, folder, StringComparison.OrdinalIgnoreCase))
        {
            await this.RefreshAsync(ct).ConfigureAwait(false);
        }
        await this.WriteAsync(tag + " OK " + code + "APPEND done\r\n", ct).ConfigureAwait(false);
    }
}
