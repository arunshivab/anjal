namespace Anjal.Mailbox;

/// <summary>
/// Hard quota enforcement at RCPT time. When the recipient resolves to a
/// mailbox whose <see cref="Anjal.Store.MailboxRow.UsedBytes"/> is at or
/// above its <see cref="Anjal.Store.MailboxRow.QuotaBytes"/>, the recipient
/// is refused with <c>452 4.2.2 Mailbox full</c> - a temporary code, so the
/// sending server queues and retries for a few days (RFC 5321 §4.5.4.1)
/// while the owner makes room. Recipients that are not mailboxes (webhook
/// targets, unknown addresses) are not affected; a quota of 0 means
/// unlimited. Authenticated submissions are not checked here: the
/// webmail refuses to compose when full, and a submitting client's own
/// mailbox usage is not a reason to refuse delivery to someone else.
/// </summary>
public sealed class QuotaPolicy : Anjal.Smtp.ISmtpPolicy
{
    private readonly Anjal.Store.IMailboxStore store;
    private readonly System.Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="store">Mailbox registry.</param>
    /// <param name="log">Optional log sink.</param>
    public QuotaPolicy(Anjal.Store.IMailboxStore store, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.log = log;
    }

    /// <summary>Whether a mailbox is at or over its hard quota.</summary>
    /// <param name="mailbox">The mailbox.</param>
    public static bool IsFull(Anjal.Store.MailboxRow mailbox)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);
        return mailbox.QuotaBytes > 0 && mailbox.UsedBytes >= mailbox.QuotaBytes;
    }

    /// <inheritdoc/>
    public System.Threading.Tasks.Task<Anjal.Smtp.PolicyDecision> OnConnectAsync(string remoteAddress, System.Threading.CancellationToken ct = default) =>
        System.Threading.Tasks.Task.FromResult(Anjal.Smtp.PolicyDecision.Allow);

    /// <inheritdoc/>
    public System.Threading.Tasks.Task<Anjal.Smtp.PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, System.Threading.CancellationToken ct = default) =>
        System.Threading.Tasks.Task.FromResult(Anjal.Smtp.PolicyDecision.Allow);

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(recipient);
        if (!MailboxSink.TrySplitAddress(recipient, out string local, out string domain))
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }
        Anjal.Store.MailboxRow? mailbox = await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false);
        if (mailbox is null || !IsFull(mailbox))
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }
        this.log?.Invoke($"Quota: refusing RCPT {recipient} - mailbox {mailbox.Address} full ({mailbox.UsedBytes} of {mailbox.QuotaBytes} bytes)");
        Anjal.Smtp.Counters.Increment("anjal_quota_refusals_total");
        return Anjal.Smtp.PolicyDecision.Defer("4.2.2 Mailbox full", 452);
    }
}
