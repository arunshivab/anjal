using System.Text.Json;

namespace Anjal.Mailbox;

/// <summary>
/// rc.26 (item 63, POL-01 v1.1 §4, D-166; approved by the owner 26 Oct 2026): a mailbox nobody has signed
/// in to for 180 days becomes inactive - incoming mail is refused, telling the sender to ask the person
/// for another address - and 186 days after that it is deleted, with reminders before. Sign-ins through
/// the webmail, a passkey, or a mail app on port 587 all count. Not counted: shared mailboxes, application
/// inboxes, and a mailbox under legal hold (when a hold is lifted the clock starts again). These periods
/// are Anjal policy; an organisation cannot change them.
/// </summary>
public static class InactiveAccounts
{
    /// <summary>Without a sign-in for this long, a mailbox is inactive.</summary>
    public static readonly System.TimeSpan InactiveAfter = System.TimeSpan.FromDays(180);

    /// <summary>Inactive for this long, it is deleted.</summary>
    public static readonly System.TimeSpan DeletedAfter = System.TimeSpan.FromDays(186);

    /// <summary>Reminders go this many days before deletion.</summary>
    public static readonly IReadOnlyList<int> ReminderDays = new[] { 60, 30, 15, 7, 3, 1 };

    /// <summary>The reply to a sender (550 5.1.1).</summary>
    public const string RefusalText = "This mailbox is inactive; please ask the person for another address";

    /// <summary>When a mailbox becomes inactive.</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <returns>The moment.</returns>
    public static System.DateTimeOffset InactiveFrom(Anjal.Store.MailboxRow mailbox)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);
        return mailbox.LastSignInAt + InactiveAfter;
    }

    /// <summary>When an inactive mailbox is deleted.</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <returns>The moment.</returns>
    public static System.DateTimeOffset DeletedOn(Anjal.Store.MailboxRow mailbox) => InactiveFrom(mailbox) + DeletedAfter;

    /// <summary>Whether the inactive clock applies to a mailbox: a person's own, not shared, not under legal hold.</summary>
    /// <param name="store">The store.</param>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it counts.</returns>
    public static async System.Threading.Tasks.Task<bool> CountsAsync(Anjal.Store.IMailboxStore store, Anjal.Store.MailboxRow mailbox, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(mailbox);
        if (mailbox.Kind != Anjal.Store.MailboxRow.PersonKind)
        {
            return false;
        }
        // The organisation's shared mailboxes (a document keyed by mailbox id) and legal holds (a list with MailboxId).
        string id = mailbox.Id.ToString();
        if (await store.GetTenantDocumentAsync(mailbox.TenantId, "shared", ct).ConfigureAwait(false) is { Length: > 0 } shared)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(shared);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, id, System.StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }
        if (await store.GetTenantDocumentAsync(mailbox.TenantId, "holds", ct).ConfigureAwait(false) is { Length: > 0 } holds)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(holds);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.EnumerateArray().Any(h =>
                    h.TryGetProperty("MailboxId", out JsonElement m) && string.Equals(m.GetString(), id, System.StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a mailbox is inactive now.</summary>
    /// <param name="store">The store.</param>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when inactive.</returns>
    public static async System.Threading.Tasks.Task<bool> IsInactiveAsync(Anjal.Store.IMailboxStore store, Anjal.Store.MailboxRow mailbox, System.DateTimeOffset now, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);
        return now >= InactiveFrom(mailbox) && await CountsAsync(store, mailbox, ct).ConfigureAwait(false);
    }
}

/// <summary>rc.26 (item 63): mail to an inactive mailbox is refused at RCPT, from outside and inside alike.</summary>
public sealed class InactivePolicy : Anjal.Smtp.ISmtpPolicy
{
    private readonly Anjal.Store.IMailboxStore store;
    private readonly System.Func<System.DateTimeOffset> clock;
    private readonly System.Action<string>? log;

    /// <summary>Initializes a new instance of the <see cref="InactivePolicy"/> class.</summary>
    /// <param name="store">The mailbox store.</param>
    /// <param name="log">Optional log.</param>
    /// <param name="clock">Clock, for tests.</param>
    public InactivePolicy(Anjal.Store.IMailboxStore store, System.Action<string>? log = null, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.log = log;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
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
        try
        {
            if (MailboxSink.TrySplitAddress(recipient, out string local, out string domain)
                && await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is Anjal.Store.MailboxRow mailbox
                && await InactiveAccounts.IsInactiveAsync(this.store, mailbox, this.clock(), ct).ConfigureAwait(false))
            {
                this.log?.Invoke($"Inactive: refusing RCPT {recipient}");
                return Anjal.Smtp.PolicyDecision.Reject("5.1.1 " + InactiveAccounts.RefusalText, 550);
            }
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Cannot tell: accept; nothing is lost by delivering to a mailbox that may be inactive.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Inactive: cannot check {recipient} ({ex.GetType().Name}); accepting.");
        }
#pragma warning restore CA1031
        return Anjal.Smtp.PolicyDecision.Allow;
    }
}
