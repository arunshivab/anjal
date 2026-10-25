using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>What Anjal thinks of one link (rc.24, item 94).</summary>
public enum LinkVerdict
{
    /// <summary>Nothing to say.</summary>
    Fine = 0,

    /// <summary>Shown with a warning: the words and the target differ, a shortener, a bare number, mixed alphabets, a download.</summary>
    Suspicious = 1,

    /// <summary>Switched off: on the operator's list of known dangerous addresses, or on the DNS list when one is set.</summary>
    Bad = 2,
}

/// <summary>One link Anjal has something to say about.</summary>
/// <param name="Host">Where it really goes.</param>
/// <param name="Shown">The words shown for it.</param>
/// <param name="Verdict">Suspicious or bad.</param>
/// <param name="Reason">Why, in English with blanks: {host}, {shown}, {file}.</param>
/// <param name="File">The file it downloads, for a download; else empty.</param>
public sealed record LinkFinding(string Host, string Shown, LinkVerdict Verdict, string Reason, string File = "");

/// <summary>
/// rc.24 (item 94, D-197; owner, 25 Oct 2026): every link in a mail is looked at when the mail is shown.
/// A link to a known dangerous address is switched off with a clear warning; a suspicious one - words
/// that name one place while the link goes to another, a link shortener, a bare number instead of a
/// name, a name in mixed or look-alike alphabets, a link that downloads a program or archive - is left
/// working with a warning. "Known dangerous" is the operator's own list (ANJAL_LINK_BLOCKLIST, a file of
/// addresses, one per line) and, when set, a DNS list (ANJAL_LINK_DNSBL); both are off until set, because
/// the free lists do not allow use in a paid service (verified 25 Oct 2026). Only a domain name ever leaves
/// the server, and only for the DNS list. ClamAV's own phishing checks read every mail when it arrives.
/// </summary>
public sealed class LinkChecker
{
    /// <summary>The setting naming the operator's list of known dangerous addresses.</summary>
    public const string BlocklistSetting = "ANJAL_LINK_BLOCKLIST";

    /// <summary>The setting naming a DNS list zone for domain names (for example a paid Spamhaus DQS zone).</summary>
    public const string DnsListSetting = "ANJAL_LINK_DNSBL";

    /// <summary>Why a link is off: on a list of known dangerous addresses.</summary>
    public const string ListedWords = "Anjal switched this link off: {host} is on the list of known dangerous addresses.";

    /// <summary>Why every link is off: ClamAV flagged the whole mail.</summary>
    public const string MailFlaggedWords = "Anjal switched this link off: the virus scanner flagged this mail as dangerous.";

    /// <summary>The words name one place, the link goes to another.</summary>
    public const string MismatchWords = "The words show {shown}, but the link goes to {host}.";

    /// <summary>A link shortener.</summary>
    public const string ShortenerWords = "A shortened link: where it really goes is hidden ({host}).";

    /// <summary>A bare number.</summary>
    public const string NumberWords = "The address is a bare number ({host}), not a name.";

    /// <summary>Mixed or look-alike alphabets.</summary>
    public const string LookalikeWords = "The name mixes alphabets or uses look-alike letters ({host}).";

    /// <summary>A download.</summary>
    public const string DownloadWords = "It downloads a file ({file}).";

    private const int MaxLinks = 500;

    private static readonly string[] Shorteners =
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "ow.ly", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at",
        "tiny.cc", "rb.gy", "bl.ink", "t.ly", "s.id", "v.gd", "short.io", "lnkd.in", "trib.al", "shorte.st", "adf.ly",
    };

    private static readonly string[] DownloadEndings =
    {
        ".exe", ".scr", ".msi", ".bat", ".cmd", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".hta", ".ps1", ".jar", ".apk", ".lnk",
        ".zip", ".rar", ".7z", ".iso", ".img", ".vhd", ".dmg", ".docm", ".xlsm", ".pptm",
    };

    private static readonly Regex AnchorRegex = new("<a\\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HrefRegex = new("\\shref=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TitleRegex = new("\\stitle=\"[^\"]*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ShownHostRegex = new("^(?:https?://)?(?:www\\.)?((?:[a-z0-9-]+\\.)+[a-z]{2,})(?:[/:?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly string? blocklistPath;
    private readonly string? dnsZone;
    private readonly ConcurrentDictionary<string, (bool Listed, DateTimeOffset Until)> dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private (DateTime Stamp, HashSet<string> Hosts, List<string> Urls) list = (DateTime.MinValue, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<string>());
    private DateTimeOffset listChecked = DateTimeOffset.MinValue;

    /// <summary>A checker with the operator's list and DNS list, either of which may be absent.</summary>
    /// <param name="blocklistPath">The list's file, or null.</param>
    /// <param name="dnsZone">The DNS list's zone, or null.</param>
    public LinkChecker(string? blocklistPath, string? dnsZone)
    {
        this.blocklistPath = string.IsNullOrWhiteSpace(blocklistPath) ? null : blocklistPath.Trim();
        this.dnsZone = string.IsNullOrWhiteSpace(dnsZone) ? null : dnsZone.Trim().Trim('.');
    }

    /// <summary>The checker the settings ask for (<see cref="BlocklistSetting"/>, <see cref="DnsListSetting"/>).</summary>
    /// <returns>The checker.</returns>
    public static LinkChecker FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(BlocklistSetting), Environment.GetEnvironmentVariable(DnsListSetting));

    /// <summary>True when the operator's list is set.</summary>
    public bool HasBlocklist => this.blocklistPath is not null;

    /// <summary>True when a DNS list is set.</summary>
    public bool HasDnsList => this.dnsZone is not null;

    /// <summary>
    /// Look at every link in sanitized HTML: a bad one (or every one, when the whole mail was flagged) is
    /// switched off - the words stay, the link goes - and a suspicious one gets a warning outline and
    /// its real destination as its tip. Returns the HTML and what was found.
    /// </summary>
    /// <param name="html">The body, already sanitized.</param>
    /// <param name="allOff">True when ClamAV flagged the whole mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The body and the findings.</returns>
    public async Task<(string Html, IReadOnlyList<LinkFinding> Findings)> ApplyAsync(string html, bool allOff, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        var findings = new List<LinkFinding>();
        MatchCollection anchors;
        try
        {
            anchors = AnchorRegex.Matches(html);
            _ = anchors.Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return (html, findings);
        }
        if (anchors.Count == 0)
        {
            return (html, findings);
        }
        var sb = new StringBuilder(html.Length + 256);
        int pos = 0;
        int seen = 0;
        foreach (Match a in anchors)
        {
            if (++seen > MaxLinks)
            {
                break;
            }
            Match href = HrefRegex.Match(a.Groups[1].Value);
            string target = WebUtility.HtmlDecode(href.Success ? href.Groups[1].Value : string.Empty).Trim();
            string inner = a.Groups[2].Value;
            string shown = WebUtility.HtmlDecode(TagRegex.Replace(inner, " ")).Trim();
            LinkFinding? finding = allOff
                ? new LinkFinding(HostOf(target), shown, LinkVerdict.Bad, MailFlaggedWords)
                : await this.CheckAsync(target, shown, ct).ConfigureAwait(false);
            sb.Append(html, pos, a.Index - pos);
            pos = a.Index + a.Length;
            if (finding is null)
            {
                sb.Append(a.Value);
                continue;
            }
            findings.Add(finding);
            string tip = HtmlSanitizer.Escape(Fill(finding));
            if (finding.Verdict == LinkVerdict.Bad)
            {
                sb.Append("<span class=\"anjal-off\" title=\"").Append(tip).Append("\">").Append(inner).Append("</span>");
            }
            else
            {
                string attrs = TitleRegex.Replace(a.Groups[1].Value, string.Empty);
                sb.Append("<a").Append(attrs).Append(" data-anjal-warn=\"1\" title=\"").Append(tip).Append("\">").Append(inner).Append("</a>");
            }
        }
        sb.Append(html, pos, html.Length - pos);
        return (sb.ToString(), findings);
    }

    /// <summary>The finding for one link, or null when there is nothing to say.</summary>
    /// <param name="target">Where it goes.</param>
    /// <param name="shown">The words shown for it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The finding, or null.</returns>
    public async Task<LinkFinding?> CheckAsync(string target, string shown, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(shown);
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (this.IsListed(uri, host) || await this.DnsListedAsync(host, ct).ConfigureAwait(false))
        {
            return new LinkFinding(host, shown, LinkVerdict.Bad, ListedWords);
        }
        string file = Uri.UnescapeDataString(uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty);
        return Suspicion(uri, shown) is string reason ? new LinkFinding(host, shown, LinkVerdict.Suspicious, reason, file) : null;
    }

    /// <summary>The warning for a link, from the link alone (no list, no network), or null.</summary>
    /// <param name="uri">The link.</param>
    /// <param name="shown">The words shown for it.</param>
    /// <returns>The reason's words, or null.</returns>
    public static string? Suspicion(Uri uri, string shown)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(shown);
        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        string shownHost = ShownHost(shown);
        if (shownHost.Length > 0 && !SameSite(shownHost, host))
        {
            return MismatchWords;
        }
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return NumberWords;
        }
        if (host.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal)) || uri.Host.Any(c => c > 127))
        {
            return LookalikeWords;
        }
        if (Shorteners.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal)))
        {
            return ShortenerWords;
        }
        string path = uri.AbsolutePath.ToLowerInvariant();
        if (DownloadEndings.Any(e => path.EndsWith(e, StringComparison.Ordinal)))
        {
            return DownloadWords;
        }
        return null;
    }

    /// <summary>The finding's reason with its blanks filled, in English.</summary>
    /// <param name="f">The finding.</param>
    /// <returns>The words.</returns>
    public static string Fill(LinkFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return Fill(f, f.Reason);
    }

    /// <summary>A reason's words (already in the reader's language) with the finding's blanks filled.</summary>
    /// <param name="f">The finding.</param>
    /// <param name="words">The words with blanks.</param>
    /// <returns>The words.</returns>
    public static string Fill(LinkFinding f, string words)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(words);
        string file = f.File.Length > 0 ? f.File : f.Host;
        return words.Replace("{host}", f.Host, StringComparison.Ordinal)
            .Replace("{shown}", ShownHost(f.Shown).Length > 0 ? ShownHost(f.Shown) : f.Shown, StringComparison.Ordinal)
            .Replace("{file}", file, StringComparison.Ordinal);
    }

    // A domain the words themselves name ("www.sbi.co.in", "https://paypal.com/login"), or empty.
    private static string ShownHost(string shown)
    {
        string s = shown.Trim().TrimEnd('.', ',', ')').ToLowerInvariant();
        if (s.Length is 0 or > 200 || s.Contains(' ', StringComparison.Ordinal))
        {
            return string.Empty;
        }
        Match m = ShownHostRegex.Match(s);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    // The same site: one name is the other, or ends with it after a dot (www.sbi.co.in and sbi.co.in).
    private static bool SameSite(string a, string b) =>
        a == b || a.EndsWith("." + b, StringComparison.Ordinal) || b.EndsWith("." + a, StringComparison.Ordinal);

    private static string HostOf(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? u) ? u.IdnHost.TrimEnd('.').ToLowerInvariant() : string.Empty;

    private bool IsListed(Uri uri, string host)
    {
        if (this.blocklistPath is null)
        {
            return false;
        }
        (DateTime _, HashSet<string> hosts, List<string> urls) = this.CurrentList();
        string at = host;
        while (at.Length > 0)
        {
            if (hosts.Contains(at))
            {
                return true;
            }
            int dot = at.IndexOf('.', StringComparison.Ordinal);
            at = dot < 0 ? string.Empty : at[(dot + 1)..];
        }
        string full = uri.AbsoluteUri;
        return urls.Any(u => full.StartsWith(u, StringComparison.OrdinalIgnoreCase));
    }

    // The operator's list, read again when the file changes (looked at no more than once a minute).
    private (DateTime Stamp, HashSet<string> Hosts, List<string> Urls) CurrentList()
    {
        lock (this.gate)
        {
            if (DateTimeOffset.UtcNow - this.listChecked < TimeSpan.FromMinutes(1))
            {
                return this.list;
            }
            this.listChecked = DateTimeOffset.UtcNow;
            try
            {
                var info = new FileInfo(this.blocklistPath!);
                if (!info.Exists || info.LastWriteTimeUtc == this.list.Stamp)
                {
                    return this.list;
                }
                var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var urls = new List<string>();
                foreach (string raw in File.ReadLines(info.FullName))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }
                    if (line.Contains("://", StringComparison.Ordinal))
                    {
                        urls.Add(line);
                    }
                    else
                    {
                        hosts.Add(line.TrimEnd('.').ToLowerInvariant());
                    }
                }
                this.list = (info.LastWriteTimeUtc, hosts, urls);
            }
#pragma warning disable CA1031 // An unreadable list keeps the last one read.
            catch (Exception)
            {
            }
#pragma warning restore CA1031
            return this.list;
        }
    }

    private async Task<bool> DnsListedAsync(string host, CancellationToken ct)
    {
        if (this.dnsZone is null || host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return false;
        }
        if (this.dnsCache.TryGetValue(host, out (bool Listed, DateTimeOffset Until) known) && known.Until > DateTimeOffset.UtcNow)
        {
            return known.Listed;
        }
        bool listed = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            IPAddress[] answers = await System.Net.Dns.GetHostAddressesAsync(host + "." + this.dnsZone, timeout.Token).ConfigureAwait(false);
            // A listing answers in 127.0.0.0/8; 127.255.255.x are the lists' own error codes, not a listing.
            listed = answers.Any(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && a.GetAddressBytes() is [127, var second, _, _] && second != 255);
        }
#pragma warning disable CA1031 // No answer, or an error, is "not listed".
        catch (Exception)
        {
            listed = false;
        }
#pragma warning restore CA1031
        if (this.dnsCache.Count > 20_000)
        {
            this.dnsCache.Clear();
        }
        this.dnsCache[host] = (listed, DateTimeOffset.UtcNow.AddHours(1));
        return listed;
    }
}
