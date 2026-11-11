using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>rc.28 (DES-20 §3): FETCH and SEARCH.</summary>
internal sealed partial class ImapSession
{
    private static readonly string[] AllItems = { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE" };
    private static readonly string[] FastItems = { "FLAGS", "INTERNALDATE", "RFC822.SIZE" };
    private static readonly string[] FullItems = { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY" };

    private async Task FetchAsync(string tag, bool uid, List<ImapToken> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await this.WriteAsync(tag + " BAD FETCH needs a set and what to fetch\r\n", ct).ConfigureAwait(false);
            return;
        }
        List<string> items = args[1].Kind == 'l' ? args[1].Items.Select(x => x.Text).ToList() : args.Skip(1).Select(x => x.Text).ToList();
        items = items.SelectMany(i => i.ToUpperInvariant() switch
        {
            "ALL" => AllItems,
            "FAST" => FastItems,
            "FULL" => FullItems,
            _ => new[] { i },
        }).ToList();
        if (uid && !items.Any(i => string.Equals(i, "UID", StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, "UID");
        }
        foreach ((int seq, ImapMessage m) in this.Pick(args[0].Text, uid))
        {
            var parts = new List<object> { "* " + seq.ToString(CultureInfo.InvariantCulture) + " FETCH (" };
            bool first = true;
            // Reading a body without PEEK marks the mail read (RFC 3501 §6.4.5); done first, so FLAGS says so.
            bool setSeen = !this.readOnly && !m.Seen && items.Any(i => i.ToUpperInvariant() is "RFC822" or "RFC822.TEXT" || i.StartsWith("BODY[", StringComparison.OrdinalIgnoreCase));
            if (setSeen)
            {
                await this.account!.SetFlagsAsync(m.Id, true, m.Flagged, m.Answered, ct).ConfigureAwait(false);
                m.Seen = true;
            }
            void Add(string text)
            {
                parts.Add((first ? string.Empty : " ") + text);
                first = false;
            }
            foreach (string item in items)
            {
                string u = item.ToUpperInvariant();
                switch (u)
                {
                    case "UID":
                        Add("UID " + m.Uid.ToString(CultureInfo.InvariantCulture));
                        continue;
                    case "FLAGS":
                        Add("FLAGS " + this.FlagsOf(m));
                        continue;
                    case "INTERNALDATE":
                        Add("INTERNALDATE " + ImapText.Date(m.Received));
                        continue;
                    case "RFC822.SIZE":
                        Add("RFC822.SIZE " + (await this.SizeAsync(m, ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture));
                        continue;
                    default:
                        break;
                }
                if (await this.RawAsync(m, ct).ConfigureAwait(false) is not (byte[] raw, ImapMime mime))
                {
                    continue;
                }
                switch (u)
                {
                    case "ENVELOPE":
                        Add("ENVELOPE " + mime.Envelope());
                        break;
                    case "BODYSTRUCTURE":
                        Add("BODYSTRUCTURE " + mime.Structure(extended: true));
                        break;
                    case "BODY":
                        Add("BODY " + mime.Structure(extended: false));
                        break;
                    case "RFC822":
                        Add("RFC822 ");
                        AddLiteral(parts, raw);
                        break;
                    case "RFC822.HEADER":
                        Add("RFC822.HEADER ");
                        AddLiteral(parts, mime.HeaderBytes());
                        break;
                    case "RFC822.TEXT":
                        Add("RFC822.TEXT ");
                        AddLiteral(parts, mime.BodyBytes());
                        break;
                    default:
                        if (u.StartsWith("BODY[", StringComparison.Ordinal) || u.StartsWith("BODY.PEEK[", StringComparison.Ordinal))
                        {
                            (string name, byte[] data) = Section(item, mime);
                            Add(name + " ");
                            AddLiteral(parts, data);
                        }
                        break;
                }
            }
            if (setSeen && !items.Any(i => string.Equals(i, "FLAGS", StringComparison.OrdinalIgnoreCase)))
            {
                Add("FLAGS " + this.FlagsOf(m));
            }
            parts.Add(")\r\n");
            await this.WriteAsync(parts, ct).ConfigureAwait(false);
        }
        await this.WriteAsync(tag + " OK FETCH done\r\n", ct).ConfigureAwait(false);
    }

    private static void AddLiteral(List<object> parts, byte[] data)
    {
        parts.Add("{" + data.Length.ToString(CultureInfo.InvariantCulture) + "}\r\n");
        parts.Add(data);
    }

    // BODY[section]<origin.length>: the name answered with, and the bytes.
    private static (string Name, byte[] Data) Section(string item, ImapMime mime)
    {
        int open = item.IndexOf('[', StringComparison.Ordinal);
        int close = item.LastIndexOf(']');
        string section = close > open ? item[(open + 1)..close] : string.Empty;
        string partial = close >= 0 && close + 1 < item.Length ? item[(close + 1)..] : string.Empty;
        string upper = section.ToUpperInvariant();
        // The numbers first ("1.2"), then what of that part.
        int k = 0;
        var numbers = new List<int>();
        while (k < upper.Length)
        {
            int dot = upper.IndexOf('.', k);
            string piece = dot < 0 ? upper[k..] : upper[k..dot];
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                break;
            }
            numbers.Add(n);
            k = dot < 0 ? upper.Length : dot + 1;
        }
        string what = k < upper.Length ? upper[k..] : string.Empty;
        ImapMime? part = numbers.Count == 0 ? mime : mime.PartAt(numbers);
        byte[] data = Array.Empty<byte>();
        if (part is not null)
        {
            ImapMime inner = part.Type == "message" && part.Message is not null && numbers.Count > 0 ? part.Message : part;
            if (what.Length == 0)
            {
                data = numbers.Count == 0 ? mime.Raw : part.BodyBytes();
            }
            else if (what == "HEADER")
            {
                data = (numbers.Count == 0 ? mime : inner).HeaderBytes();
            }
            else if (what == "TEXT")
            {
                data = (numbers.Count == 0 ? mime : inner).BodyBytes();
            }
            else if (what == "MIME" && numbers.Count > 0)
            {
                data = part.HeaderBytes();
            }
            else if (what.StartsWith("HEADER.FIELDS", StringComparison.Ordinal))
            {
                bool not = what.StartsWith("HEADER.FIELDS.NOT", StringComparison.Ordinal);
                int lp = section.IndexOf('(', StringComparison.Ordinal);
                int rp = section.LastIndexOf(')');
                string[] names = lp >= 0 && rp > lp ? section[(lp + 1)..rp].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('"')).ToArray() : Array.Empty<string>();
                data = (numbers.Count == 0 ? mime : inner).HeaderFields(names, not);
            }
        }
        string name = "BODY[" + section + "]";
        if (partial.StartsWith('<') && partial.EndsWith('>'))
        {
            string[] ol = partial[1..^1].Split('.');
            if (long.TryParse(ol[0], NumberStyles.None, CultureInfo.InvariantCulture, out long origin))
            {
                long length = ol.Length > 1 && long.TryParse(ol[1], NumberStyles.None, CultureInfo.InvariantCulture, out long l) ? l : long.MaxValue;
                long from = Math.Min(origin, data.LongLength);
                long count = Math.Min(length, data.LongLength - from);
                data = data.AsSpan((int)from, (int)count).ToArray();
                name += "<" + origin.ToString(CultureInfo.InvariantCulture) + ">";
            }
        }
        return (name, data);
    }

    private async Task SearchAsync(string tag, bool uid, List<ImapToken> args, CancellationToken ct)
    {
        List<ImapToken> keys = args;
        if (keys.Count >= 2 && keys[0].Is("CHARSET"))
        {
            keys = keys.Skip(2).ToList();
        }
        var found = new List<long>();
        for (int i = 0; i < this.messages.Count; i++)
        {
            ImapMessage m = this.messages[i];
            int at = 0;
            if (await this.AllAsync(keys, ref0: at, i + 1, m, ct).ConfigureAwait(false))
            {
                found.Add(uid ? m.Uid : i + 1);
            }
        }
        await this.WriteAsync("* SEARCH" + string.Concat(found.Select(n => " " + n.ToString(CultureInfo.InvariantCulture))) + "\r\n" + tag + " OK SEARCH done\r\n", ct).ConfigureAwait(false);
    }

    // Every key must hold (a list of keys is AND).
    private async Task<bool> AllAsync(List<ImapToken> keys, int ref0, int seq, ImapMessage m, CancellationToken ct, int depth = 0)
    {
        int i = ref0;
        while (i < keys.Count)
        {
            (bool ok, int next) = await this.KeyAsync(keys, i, seq, m, ct, depth).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }
            i = next;
        }
        return true;
    }

    // Keys nested past this (NOT NOT NOT ..., lists in lists) are refused rather than followed.
    private const int MaxSearchDepth = 64;

    private async Task<(bool Ok, int Next)> KeyAsync(List<ImapToken> k, int i, int seq, ImapMessage m, CancellationToken ct, int depth = 0)
    {
        if (depth > MaxSearchDepth || i >= k.Count)
        {
            throw new FormatException("The search keys are incomplete or nested too deeply.");
        }
        ImapToken key = k[i];
        if (key.Kind == 'l')
        {
            return (await this.AllAsync(key.Items, 0, seq, m, ct, depth + 1).ConfigureAwait(false), i + 1);
        }
        string w = key.Text.ToUpperInvariant();
        string Arg(int n) => i + n < k.Count ? k[i + n].Text : string.Empty;
        DateTimeOffset day = m.Received.UtcDateTime.Date;
        switch (w)
        {
            case "ALL":
            case "OLD":
                return (true, i + 1);
            case "RECENT":
            case "KEYWORD":
                return (false, w == "KEYWORD" ? i + 2 : i + 1);
            case "UNKEYWORD":
                return (true, i + 2);
            case "NEW":
            case "UNSEEN":
                return (!m.Seen, i + 1);
            case "SEEN":
                return (m.Seen, i + 1);
            case "FLAGGED":
                return (m.Flagged, i + 1);
            case "UNFLAGGED":
                return (!m.Flagged, i + 1);
            case "ANSWERED":
                return (m.Answered, i + 1);
            case "UNANSWERED":
                return (!m.Answered, i + 1);
            case "DELETED":
                return (m.Deleted, i + 1);
            case "UNDELETED":
                return (!m.Deleted, i + 1);
            case "DRAFT":
                return (this.view?.Folder.SpecialUse == "\\Drafts", i + 1);
            case "UNDRAFT":
                return (this.view?.Folder.SpecialUse != "\\Drafts", i + 1);
            case "BEFORE":
            case "SENTBEFORE":
                return (ImapText.ParseDate(Arg(1)) is DateTimeOffset b && day < b.UtcDateTime.Date, i + 2);
            case "ON":
            case "SENTON":
                return (ImapText.ParseDate(Arg(1)) is DateTimeOffset o && day == o.UtcDateTime.Date, i + 2);
            case "SINCE":
            case "SENTSINCE":
                return (ImapText.ParseDate(Arg(1)) is DateTimeOffset s && day >= s.UtcDateTime.Date, i + 2);
            case "LARGER":
                return (await this.SizeAsync(m, ct).ConfigureAwait(false) > long.Parse(Arg(1), CultureInfo.InvariantCulture), i + 2);
            case "SMALLER":
                return (await this.SizeAsync(m, ct).ConfigureAwait(false) < long.Parse(Arg(1), CultureInfo.InvariantCulture), i + 2);
            case "UID":
                return (ImapText.ParseSet(Arg(1), this.MaxUid) is { } inUids && inUids(m.Uid), i + 2);
            case "NOT":
                (bool inner, int after) = await this.KeyAsync(k, i + 1, seq, m, ct, depth + 1).ConfigureAwait(false);
                return (!inner, after);
            case "OR":
                (bool a, int afterA) = await this.KeyAsync(k, i + 1, seq, m, ct, depth + 1).ConfigureAwait(false);
                (bool b2, int afterB) = await this.KeyAsync(k, afterA, seq, m, ct, depth + 1).ConfigureAwait(false);
                return (a || b2, afterB);
            case "FROM":
            case "TO":
            case "CC":
            case "BCC":
            case "SUBJECT":
                return (await this.HeaderHasAsync(m, w, Arg(1), ct).ConfigureAwait(false), i + 2);
            case "HEADER":
                return (await this.HeaderHasAsync(m, Arg(1), Arg(2), ct).ConfigureAwait(false), i + 3);
            case "BODY":
            case "TEXT":
                return (await this.RawAsync(m, ct).ConfigureAwait(false) is (byte[] raw, ImapMime mime) && Contains(w == "BODY" ? mime.BodyBytes() : raw, Arg(1)), i + 2);
            default:
                // A set of sequence numbers.
                return (ImapText.ParseSet(key.Text, this.messages.Count) is { } inSet && inSet(seq), i + 1);
        }
    }

    private async Task<bool> HeaderHasAsync(ImapMessage m, string field, string text, CancellationToken ct)
    {
        if (await this.RawAsync(m, ct).ConfigureAwait(false) is not (byte[] _, ImapMime mime))
        {
            return false;
        }
        string value = mime.Headers.Where(h => string.Equals(h.Name, field, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault() ?? string.Empty;
        string decoded = Anjal.Mime.EncodedWordDecoder.Decode(value);
        return text.Length == 0 ? value.Length > 0 || mime.Headers.Any(h => string.Equals(h.Name, field, StringComparison.OrdinalIgnoreCase))
            : decoded.Contains(text, StringComparison.OrdinalIgnoreCase) || value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(byte[] data, string text) =>
        text.Length == 0 || Encoding.UTF8.GetString(data).Contains(text, StringComparison.OrdinalIgnoreCase);
}
