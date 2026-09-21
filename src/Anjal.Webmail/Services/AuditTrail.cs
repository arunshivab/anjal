using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Records security-relevant webmail actions in the audit trail: sign-ins
/// (successful, refused, throttled), sign-outs, and changes to password and
/// display name. Never records a password or any message content. A failure
/// to record is swallowed: the audit trail must never be the reason a user
/// cannot sign in.
/// </summary>
public sealed class AuditTrail
{
    private readonly IMessageStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Where entries are appended.</param>
    public AuditTrail(IMessageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>Append one entry; never throws.</summary>
    /// <param name="actor">Who acted (an address, or "anonymous").</param>
    /// <param name="action">What happened, e.g. <c>webmail.signin.failed</c>.</param>
    /// <param name="subject">What it concerned.</param>
    /// <param name="remoteAddress">Client address.</param>
    /// <param name="detail">Optional short context.</param>
    public async Task RecordAsync(string actor, string action, string subject, string remoteAddress, string detail = "")
    {
        try
        {
            await this.store.AppendAuditAsync(new AuditEvent
            {
                Actor = actor ?? string.Empty,
                Action = action ?? string.Empty,
                Subject = subject ?? string.Empty,
                Detail = detail ?? string.Empty,
                RemoteAddress = remoteAddress ?? string.Empty,
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Auditing must never block the action it records.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
