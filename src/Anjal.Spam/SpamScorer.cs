using System.Globalization;
using System.Text;
using Anjal.Auth;
using Anjal.Mime;
using Anjal.Smtp;

namespace Anjal.Spam;

/// <summary>One scoring rule that fired.</summary>
public sealed class SpamReason
{
    /// <summary>Short stable code, e.g. <c>SPF_FAIL</c>.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Points contributed (may be negative for allow-listing style rules in future).</summary>
    public int Points { get; init; }

    /// <summary>Human-readable detail.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>The scorer's output for one message.</summary>
public sealed class SpamVerdict
{
    /// <summary>Total points.</summary>
    public int Score { get; init; }

    /// <summary>Rules that fired, in evaluation order.</summary>
    public IReadOnlyList<SpamReason> Reasons { get; init; } = Array.Empty<SpamReason>();

    /// <summary>
    /// Header-friendly reasons: <c>CODE(points)</c> joined by commas.
    /// </summary>
    public string ReasonsHeaderValue
    {
        get
        {
            var sb = new StringBuilder();
            foreach (SpamReason r in this.Reasons)
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(r.Code).Append('(').Append(r.Points.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
            return sb.ToString();
        }
    }
}

/// <summary>Tunable weights and lists for <see cref="SpamScorer"/>.</summary>
public sealed class SpamScorerOptions
{
    /// <summary>Points for SPF fail.</summary>
    public int SpfFail { get; init; } = 3;

    /// <summary>Points for SPF softfail.</summary>
    public int SpfSoftFail { get; init; } = 2;

    /// <summary>Points when the sender domain publishes no SPF record.</summary>
    public int SpfNone { get; init; } = 1;

    /// <summary>Points for a DKIM signature that fails verification.</summary>
    public int DkimFail { get; init; } = 3;

    /// <summary>Points when the message carries no DKIM signature.</summary>
    public int DkimNone { get; init; } = 1;

    /// <summary>Points when DMARC evaluates to fail (whatever the published policy).</summary>
    public int DmarcFail { get; init; } = 3;

    /// <summary>Points when the sender domain cannot receive mail (no MX and no A/AAAA).</summary>
    public int SenderDomainUnreachable { get; init; } = 3;

    /// <summary>Points when HELO is a bare IP address or has no dot.</summary>
    public int HeloMalformed { get; init; } = 2;

    /// <summary>Points when HELO is well-formed but does not resolve.</summary>
    public int HeloUnresolvable { get; init; } = 1;

    /// <summary>Points when the connecting IP has no reverse DNS.</summary>
    public int NoReverseDns { get; init; } = 1;

    /// <summary>Points when the From header domain differs from the envelope sender domain.</summary>
    public int FromEnvelopeMismatch { get; init; } = 1;

    /// <summary>Points when the From header is missing or empty.</summary>
    public int MissingFrom { get; init; } = 2;

    /// <summary>Points when Message-ID is missing.</summary>
    public int MissingMessageId { get; init; } = 1;

    /// <summary>Points when Date is missing.</summary>
    public int MissingDate { get; init; } = 1;

    /// <summary>Points when the subject is entirely upper-case (8+ letters).</summary>
    public int SubjectAllCaps { get; init; } = 1;

    /// <summary>Points per matched phrase, capped by <see cref="PhraseMaxPoints"/>.</summary>
    public int PhrasePoints { get; init; } = 1;

    /// <summary>Cap on phrase points per message.</summary>
    public int PhraseMaxPoints { get; init; } = 3;

    /// <summary>Recipient count above which <see cref="ManyRecipients"/> applies.</summary>
    public int ManyRecipientsThreshold { get; init; } = 20;

    /// <summary>Points for an envelope with many recipients.</summary>
    public int ManyRecipients { get; init; } = 1;

    /// <summary>
    /// Phrases (case-insensitive substring match on subject and text body)
    /// that add <see cref="PhrasePoints"/> each. Intentionally short and
    /// editable; a real corpus-trained filter is a later release.
    /// </summary>
    public IReadOnlyList<string> Phrases { get; init; } = new[]
    {
        "act now", "100% free", "click here", "you have won", "winner", "lottery", "wire transfer",
        "guaranteed", "risk-free", "limited time", "urgent response", "dear friend", "million dollars",
        "unsubscribe here", "casino", "viagra", "cheap meds", "crypto giveaway", "verify your account",
    };
}

/// <summary>
/// Minimum-heuristics spam scorer. Adds points for each signal that
/// fires and returns the total with the reasons; the caller compares the
/// score to the tenant's threshold. Authentication results are read from
/// <see cref="DeliveryContext.AuthResults"/> when present. DNS questions
/// go through <see cref="ISpamDnsLookup"/> and are skipped when no
/// lookup is configured.
/// </summary>
public sealed class SpamScorer
{
    private readonly SpamScorerOptions options;
    private readonly ISpamDnsLookup? dns;

    /// <summary>Construct.</summary>
    /// <param name="options">Weights; null for defaults.</param>
    /// <param name="dns">DNS lookups; null disables the DNS-based rules.</param>
    public SpamScorer(SpamScorerOptions? options = null, ISpamDnsLookup? dns = null)
    {
        this.options = options ?? new SpamScorerOptions();
        this.dns = dns;
    }

    /// <summary>The options in effect.</summary>
    public SpamScorerOptions Options => this.options;

    /// <summary>
    /// Score a delivery. Never throws: a rule that cannot be evaluated is
    /// skipped.
    /// </summary>
    /// <param name="ctx">The delivery context.</param>
    /// <param name="parsed">The parsed message, or null if parsing failed.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SpamVerdict> ScoreAsync(DeliveryContext ctx, MimeMessage? parsed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var reasons = new List<SpamReason>();

        this.ScoreAuthentication(ctx, reasons);
        await this.ScoreDnsAsync(ctx, reasons, ct).ConfigureAwait(false);
        this.ScoreHeaders(ctx, parsed, reasons);
        this.ScoreContent(parsed, reasons);
        if (ctx.EnvelopeTo.Count > this.options.ManyRecipientsThreshold)
        {
            reasons.Add(new SpamReason { Code = "MANY_RCPT", Points = this.options.ManyRecipients, Detail = $"{ctx.EnvelopeTo.Count} recipients" });
        }

        int total = 0;
        foreach (SpamReason r in reasons)
        {
            total += r.Points;
        }
        return new SpamVerdict { Score = total, Reasons = reasons };
    }

    private void ScoreAuthentication(DeliveryContext ctx, List<SpamReason> reasons)
    {
        if (ctx.AuthResults is not AuthenticationResults auth)
        {
            return;
        }
        switch (auth.Spf.Result)
        {
            case SpfResult.Fail:
                reasons.Add(new SpamReason { Code = "SPF_FAIL", Points = this.options.SpfFail, Detail = auth.Spf.Explanation });
                break;
            case SpfResult.SoftFail:
                reasons.Add(new SpamReason { Code = "SPF_SOFTFAIL", Points = this.options.SpfSoftFail, Detail = auth.Spf.Explanation });
                break;
            case SpfResult.None:
                reasons.Add(new SpamReason { Code = "SPF_NONE", Points = this.options.SpfNone, Detail = "sender domain publishes no SPF" });
                break;
        }
        switch (auth.Dkim.Result)
        {
            case DkimResult.Fail:
                reasons.Add(new SpamReason { Code = "DKIM_FAIL", Points = this.options.DkimFail, Detail = auth.Dkim.Explanation });
                break;
            case DkimResult.None:
                reasons.Add(new SpamReason { Code = "DKIM_NONE", Points = this.options.DkimNone, Detail = "no DKIM signature" });
                break;
        }
        if (auth.Dmarc.Result == DmarcResult.Fail)
        {
            reasons.Add(new SpamReason { Code = "DMARC_FAIL", Points = this.options.DmarcFail, Detail = auth.Dmarc.Explanation });
        }
    }

    private async Task ScoreDnsAsync(DeliveryContext ctx, List<SpamReason> reasons, CancellationToken ct)
    {
        string helo = ctx.ClientHostName.Trim();
        if (helo.Length > 0)
        {
            bool bareIp = System.Net.IPAddress.TryParse(helo.Trim('[', ']'), out _);
            if (bareIp || !helo.Contains('.', StringComparison.Ordinal))
            {
                reasons.Add(new SpamReason { Code = "HELO_MALFORMED", Points = this.options.HeloMalformed, Detail = helo });
            }
            else if (this.dns is not null)
            {
                bool? resolves = await this.dns.ResolvesAsync(helo, ct).ConfigureAwait(false);
                if (resolves == false)
                {
                    reasons.Add(new SpamReason { Code = "HELO_UNRESOLVABLE", Points = this.options.HeloUnresolvable, Detail = helo });
                }
            }
        }

        if (this.dns is null)
        {
            return;
        }

        string senderDomain = DomainOf(ctx.EnvelopeFrom);
        if (senderDomain.Length > 0)
        {
            bool? can = await this.dns.CanReceiveMailAsync(senderDomain, ct).ConfigureAwait(false);
            if (can == false)
            {
                reasons.Add(new SpamReason { Code = "SENDER_DOMAIN_UNREACHABLE", Points = this.options.SenderDomainUnreachable, Detail = senderDomain });
            }
        }

        if (ctx.RemoteAddress.Length > 0 && !IsLoopbackOrPrivate(ctx.RemoteAddress))
        {
            bool? rdns = await this.dns.HasReverseDnsAsync(ctx.RemoteAddress, ct).ConfigureAwait(false);
            if (rdns == false)
            {
                reasons.Add(new SpamReason { Code = "NO_RDNS", Points = this.options.NoReverseDns, Detail = ctx.RemoteAddress });
            }
        }
    }

    private void ScoreHeaders(DeliveryContext ctx, MimeMessage? parsed, List<SpamReason> reasons)
    {
        if (parsed is null)
        {
            return;
        }
        string fromHeader = parsed.Headers.Get("From") ?? string.Empty;
        string fromAddress = FirstAddress(fromHeader);
        if (fromAddress.Length == 0)
        {
            reasons.Add(new SpamReason { Code = "MISSING_FROM", Points = this.options.MissingFrom, Detail = "no From header address" });
        }
        else
        {
            string envDomain = DomainOf(ctx.EnvelopeFrom);
            string fromDomain = DomainOf(fromAddress);
            if (envDomain.Length > 0 && fromDomain.Length > 0 && !SameOrSubdomain(fromDomain, envDomain))
            {
                reasons.Add(new SpamReason { Code = "FROM_MISMATCH", Points = this.options.FromEnvelopeMismatch, Detail = $"From {fromDomain}, envelope {envDomain}" });
            }
        }
        if (string.IsNullOrEmpty(parsed.MessageId))
        {
            reasons.Add(new SpamReason { Code = "NO_MESSAGE_ID", Points = this.options.MissingMessageId, Detail = "no Message-ID" });
        }
        if (string.IsNullOrEmpty(parsed.Date))
        {
            reasons.Add(new SpamReason { Code = "NO_DATE", Points = this.options.MissingDate, Detail = "no Date" });
        }
    }

    private void ScoreContent(MimeMessage? parsed, List<SpamReason> reasons)
    {
        if (parsed is null)
        {
            return;
        }
        string subject = parsed.Subject ?? string.Empty;
        if (IsAllCaps(subject))
        {
            reasons.Add(new SpamReason { Code = "SUBJECT_CAPS", Points = this.options.SubjectAllCaps, Detail = subject });
        }

        string text = subject + "\n" + FirstTextBody(parsed.Body);
        int phrasePoints = 0;
        var hits = new List<string>();
        foreach (string phrase in this.options.Phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(phrase);
                phrasePoints += this.options.PhrasePoints;
                if (phrasePoints >= this.options.PhraseMaxPoints)
                {
                    phrasePoints = this.options.PhraseMaxPoints;
                    break;
                }
            }
        }
        if (phrasePoints > 0)
        {
            reasons.Add(new SpamReason { Code = "PHRASES", Points = phrasePoints, Detail = string.Join("; ", hits) });
        }
    }

    /// <summary>Lowercase domain of an address, or empty.</summary>
    /// <param name="address">An email address.</param>
    public static string DomainOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address.Substring(at + 1).Trim().TrimEnd('>').ToLowerInvariant();
    }

    /// <summary>The first address in a header value, lowercased, or empty.</summary>
    /// <param name="headerValue">A From/Sender header value.</param>
    public static string FirstAddress(string headerValue)
    {
        ArgumentNullException.ThrowIfNull(headerValue);
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(headerValue);
        return parsed.Count > 0 ? parsed[0].Address.ToLowerInvariant() : string.Empty;
    }

    private static bool SameOrSubdomain(string domain, string parent) =>
        string.Equals(domain, parent, StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase) ||
        parent.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a subject is shouting in capitals. Only CASED letters count:
    /// Tamil, Devanagari, Malayalam, Arabic, Hebrew, Thai, Chinese, Japanese
    /// and Korean have no capitals at all, and counting their letters as
    /// "not lowercase" charged a spam point to every ordinary subject in the
    /// languages this product is built for (DEF-039).
    /// </summary>
    /// <param name="s">The subject.</param>
    private static bool IsAllCaps(string s)
    {
        int cased = 0;
        foreach (char c in s)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }
            bool upper = char.IsUpper(c);
            bool lower = char.IsLower(c);
            if (lower)
            {
                return false;
            }
            if (upper)
            {
                cased++;
            }
        }
        return cased >= 8;
    }

    private static bool IsLoopbackOrPrivate(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? ip))
        {
            return true;
        }
        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    private static string FirstTextBody(MimeEntity entity)
    {
        if (entity is MimePart part)
        {
            return part.ContentType.MimeType == "text/plain" || part.ContentType.MimeType == "text/html" ? part.GetBodyAsText() : string.Empty;
        }
        if (entity is MimeMultipart multi)
        {
            foreach (MimeEntity child in multi.Parts)
            {
                string t = FirstTextBody(child);
                if (t.Length > 0)
                {
                    return t;
                }
            }
        }
        return string.Empty;
    }
}
