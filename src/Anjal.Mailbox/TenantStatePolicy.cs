namespace Anjal.Mailbox;

/// <summary>
/// Defers mail for a disabled tenant instead of rejecting it. When a
/// recipient resolves to a mailbox whose tenant is disabled, RCPT is
/// answered with <c>450 4.2.1 Mailbox temporarily unavailable</c>: the
/// sending server keeps the message in its own queue and retries for its
/// configured window (commonly four to five days) rather than bouncing
/// immediately. Re-enabling the tenant inside that window loses nothing
/// and generates no bounce; a tenant that is never re-enabled still
/// bounces at the sender's ceiling.
/// <para>
/// A disabled <em>mailbox</em> (as opposed to tenant) is not deferred
/// here: that is a per-address decision the operator made, and the
/// mailbox sink's permanent failure is the right answer.
/// </para>
/// </summary>
public sealed class TenantStatePolicy : Anjal.Smtp.ISmtpPolicy
{
    private readonly Anjal.Store.IMailboxStore store;
    private readonly System.Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="store">Mailbox registry.</param>
    /// <param name="log">Optional log sink.</param>
    public TenantStatePolicy(Anjal.Store.IMailboxStore store, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.log = log;
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
        // This policy protects stored state. If the store cannot be read,
        // deferring is the only safe answer: the sender retries later, and a
        // database outage never becomes a window in which limits vanish.
        try
        {
            return await this.CheckAsync(recipient, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any store failure means "cannot decide": defer.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Tenant state: cannot check {recipient} ({ex.GetType().Name}); deferring.");
            return Anjal.Smtp.PolicyDecision.Defer("4.3.0 Temporary server error, try again later", 451);
        }
#pragma warning restore CA1031
    }

    private async System.Threading.Tasks.Task<Anjal.Smtp.PolicyDecision> CheckAsync(string recipient, System.Threading.CancellationToken ct)
    {
        System.ArgumentNullException.ThrowIfNull(recipient);
        if (!MailboxSink.TrySplitAddress(recipient, out string local, out string domain))
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }
        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        if (domainRow is null)
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }
        Anjal.Store.TenantRow? tenant = await this.store.GetTenantByIdAsync(domainRow.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Enabled)
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }

        // Only defer for addresses that are (or were) real mailboxes, so a
        // disabled tenant's domain does not become a catch-all sink.
        Anjal.Store.MailboxRow? mailbox = await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            return Anjal.Smtp.PolicyDecision.Allow;
        }

        this.log?.Invoke($"Tenant {tenant.Slug} is disabled; deferring RCPT {recipient}.");
        Anjal.Smtp.Counters.Increment("anjal_tenant_disabled_deferrals_total");
        return Anjal.Smtp.PolicyDecision.Defer("4.2.1 Mailbox temporarily unavailable", 450);
    }
}
