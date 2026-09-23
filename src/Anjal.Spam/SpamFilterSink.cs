using System.Globalization;
using System.Text;
using Anjal.Mime;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Spam;

/// <summary>What the filter does with a message that scores as spam.</summary>
public enum SpamAction
{
    /// <summary>Accept and mark; the mailbox sink files it in Junk. Default.</summary>
    Junk = 0,

    /// <summary>Refuse at SMTP time with 550. Only after the scoring has been validated against real mail.</summary>
    Reject = 1,
}

/// <summary>
/// Header names written by <see cref="SpamFilterSink"/> and read by the
/// mailbox sink. They are prepended to the message bytes the same way
/// <c>Authentication-Results</c> is, so they survive into the Maildir
/// file and can be inspected in any client.
/// </summary>
public static class SpamHeaders
{
    /// <summary>Integer score.</summary>
    public const string Score = "X-Anjal-Spam-Score";

    /// <summary>Comma-separated <c>CODE(points)</c> list.</summary>
    public const string Reasons = "X-Anjal-Spam-Reasons";

    /// <summary>
    /// Read the score header from a parsed message. Returns 0 when absent
    /// or unparsable.
    /// </summary>
    /// <param name="parsed">The parsed message.</param>
    public static int ScoreOf(MimeMessage? parsed)
    {
        string? raw = parsed?.Headers.Get(Score);
        return raw is not null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>
    /// Prepend the score and reasons headers to raw message bytes.
    /// Existing headers of the same name (which a sender could forge) are
    /// not removed, but ours come first and <see cref="ScoreOf"/> reads
    /// the first occurrence.
    /// </summary>
    /// <param name="raw">Original bytes.</param>
    /// <param name="verdict">The verdict to record.</param>
    public static byte[] Prepend(byte[] raw, SpamVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(verdict);
        string lines = Score + ": " + verdict.Score.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       Reasons + ": " + (verdict.Reasons.Count == 0 ? "none" : verdict.ReasonsHeaderValue) + "\r\n";
        byte[] head = Encoding.ASCII.GetBytes(lines);

        // Trace information belongs at the very top (RFC 5321 4.4), so when
        // the message starts with the Received line our session just added,
        // the spam headers go directly beneath it rather than above it.
        int at = EndOfLeadingReceived(raw);
        byte[] combined = new byte[head.Length + raw.Length];
        Buffer.BlockCopy(raw, 0, combined, 0, at);
        Buffer.BlockCopy(head, 0, combined, at, head.Length);
        Buffer.BlockCopy(raw, at, combined, at + head.Length, raw.Length - at);
        return combined;
    }

    /// <summary>
    /// The offset just past a leading "Received:" field (with its folded
    /// continuation lines), or 0 when the message does not start with one.
    /// </summary>
    private static int EndOfLeadingReceived(byte[] raw)
    {
        byte[] name = Encoding.ASCII.GetBytes("Received:");
        if (raw.Length < name.Length)
        {
            return 0;
        }
        for (int k = 0; k < name.Length; k++)
        {
            if (char.ToLowerInvariant((char)raw[k]) != char.ToLowerInvariant((char)name[k]))
            {
                return 0;
            }
        }
        for (int i = name.Length; i < raw.Length - 1; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n')
            {
                int next = i + 2;
                if (next >= raw.Length || (raw[next] != ' ' && raw[next] != '\t'))
                {
                    return next;
                }
            }
        }
        return 0;
    }
}

/// <summary>Evaluates per-tenant sender allow/block rules.</summary>
public static class SenderRules
{
    /// <summary>
    /// Whether a rule pattern is well formed: <c>local@domain</c> or
    /// <c>@domain</c>, the domain containing a dot, with no whitespace or
    /// slashes. Shared by the admin API and the webmail so both accept exactly
    /// the same patterns.
    /// </summary>
    /// <param name="pattern">Lowercased pattern.</param>
    public static bool IsValidPattern(string pattern)
    {
        System.ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length < 2 || pattern.Contains(' ', System.StringComparison.Ordinal) || pattern.Contains('/', System.StringComparison.Ordinal))
        {
            return false;
        }
        int at = pattern.IndexOf('@', System.StringComparison.Ordinal);
        if (at < 0 || at != pattern.LastIndexOf('@') || at == pattern.Length - 1)
        {
            return false;
        }
        // The domain must be real-shaped: at least two labels, none empty,
        // each letters, digits and inner hyphens. "@." used to pass (DEF-035),
        // as would "@.com" or "@example." - rules that can never match.
        string[] labels = pattern.Substring(at + 1).Split('.');
        if (labels.Length < 2)
        {
            return false;
        }
        foreach (string label in labels)
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }
            foreach (char ch in label)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Find the rule that applies to a sender. Exact-address rules beat
    /// domain rules; within the same specificity, block beats allow. Both
    /// the envelope sender and the From header address are checked.
    /// </summary>
    /// <param name="rules">The tenant's rules.</param>
    /// <param name="envelopeFrom">MAIL FROM address.</param>
    /// <param name="fromHeaderAddress">First address in the From header, or empty.</param>
    /// <returns>The action, or null if no rule matches.</returns>
    public static SenderRuleAction? Evaluate(IReadOnlyList<SenderRuleRow> rules, string envelopeFrom, string fromHeaderAddress)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(envelopeFrom);
        ArgumentNullException.ThrowIfNull(fromHeaderAddress);
        if (rules.Count == 0)
        {
            return null;
        }

        SenderRuleAction? exact = null;
        SenderRuleAction? domain = null;
        foreach (string address in new[] { envelopeFrom.Trim().ToLowerInvariant(), fromHeaderAddress.Trim().ToLowerInvariant() })
        {
            if (address.Length == 0)
            {
                continue;
            }
            string addrDomain = SpamScorer.DomainOf(address);
            foreach (SenderRuleRow rule in rules)
            {
                string pattern = rule.Pattern;
                if (pattern.StartsWith('@'))
                {
                    string d = pattern.Substring(1);
                    if (addrDomain.Length > 0 && (addrDomain == d || addrDomain.EndsWith("." + d, StringComparison.Ordinal)))
                    {
                        domain = Merge(domain, rule.Action);
                    }
                }
                else if (pattern == address)
                {
                    exact = Merge(exact, rule.Action);
                }
            }
        }
        return exact ?? domain;
    }

    private static SenderRuleAction Merge(SenderRuleAction? current, SenderRuleAction next) =>
        current == SenderRuleAction.Block || next == SenderRuleAction.Block ? SenderRuleAction.Block : SenderRuleAction.Allow;
}

/// <summary>
/// <see cref="IMessageSink"/> decorator that scores every unauthenticated
/// delivery, records the verdict in <see cref="SpamHeaders"/> headers,
/// and hands the annotated message to the inner sink. Authenticated
/// (submission) mail passes through untouched. In <see cref="SpamAction.Reject"/>
/// mode a message at or above <see cref="RejectThreshold"/> is refused
/// with 550 instead of being passed on.
/// </summary>
public sealed class SpamFilterSink : IMessageSink
{
    private readonly SpamScorer scorer;
    private readonly IMessageSink inner;
    private readonly Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="scorer">The scorer.</param>
    /// <param name="inner">Sink that receives the annotated message.</param>
    /// <param name="log">Optional log sink.</param>
    public SpamFilterSink(SpamScorer scorer, IMessageSink inner, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(inner);
        this.scorer = scorer;
        this.inner = inner;
        this.log = log;
    }

    /// <summary>What to do with spam. Default <see cref="SpamAction.Junk"/>.</summary>
    public SpamAction Action { get; init; } = SpamAction.Junk;

    /// <summary>Score at or above which <see cref="SpamAction.Reject"/> refuses the message. Default 5.</summary>
    public int RejectThreshold { get; init; } = TenantRow.DefaultSpamThreshold;

    /// <inheritdoc/>
    public async Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.AuthenticatedUser is not null)
        {
            return await this.inner.DeliverAsync(ctx, ct).ConfigureAwait(false);
        }

        MimeMessage? parsed;
        try
        {
            parsed = MimeParser.Parse(ctx.RawBytes);
        }
#pragma warning disable CA1031 // Unparseable mail is scored on envelope/DNS signals only.
        catch (Exception)
        {
            parsed = null;
        }
#pragma warning restore CA1031

        SpamVerdict verdict;
        try
        {
            verdict = await this.scorer.ScoreAsync(ctx, parsed, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A scorer failure must not lose mail: deliver unscored.
        catch (Exception ex)
        {
            this.log?.Invoke($"Spam: scoring failed, delivering unscored: {ex.GetType().Name}: {ex.Message}");
            return await this.inner.DeliverAsync(ctx, ct).ConfigureAwait(false);
        }
#pragma warning restore CA1031

        this.log?.Invoke($"Spam: score {verdict.Score} for <{ctx.EnvelopeFrom}> from {ctx.RemoteAddress}: {(verdict.Reasons.Count == 0 ? "none" : verdict.ReasonsHeaderValue)}");

        Counters.Increment("anjal_spam_scored_total");
        if (this.Action == SpamAction.Reject && verdict.Score >= this.RejectThreshold)
        {
            Counters.Increment("anjal_spam_rejected_total");
            return new DeliveryResult
            {
                Outcome = DeliveryOutcome.PermanentFailure,
                ReplyText = "5.7.1 Message refused by content policy",
            };
        }

        var annotated = new DeliveryContext
        {
            EnvelopeFrom = ctx.EnvelopeFrom,
            EnvelopeTo = ctx.EnvelopeTo,
            RawBytes = SpamHeaders.Prepend(ctx.RawBytes, verdict),
            RemoteAddress = ctx.RemoteAddress,
            ClientHostName = ctx.ClientHostName,
            AuthenticatedUser = ctx.AuthenticatedUser,
            AuthResults = ctx.AuthResults,
        };
        return await this.inner.DeliverAsync(annotated, ct).ConfigureAwait(false);
    }
}
