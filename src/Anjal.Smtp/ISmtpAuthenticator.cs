namespace Anjal.Smtp;

/// <summary>
/// Pluggable authenticator for SMTP submission. Called when a client
/// issues <c>AUTH PLAIN</c> or <c>AUTH LOGIN</c> on a submission port.
/// Implementations look up the credentials (env-var, database, LDAP, etc.)
/// and either return an <see cref="AuthenticatedUser"/> or null.
/// </summary>
public interface ISmtpAuthenticator
{
    /// <summary>
    /// Verify credentials. Returns the authenticated user on success, or
    /// null on any failure (bad password, unknown user, disabled account).
    /// Implementations MUST NOT distinguish between these failure modes
    /// in the return value - that's important to prevent user enumeration.
    /// </summary>
    /// <param name="username">SMTP AUTH username submitted by the client.</param>
    /// <param name="password">SMTP AUTH password submitted by the client.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<AuthenticatedUser?> AuthenticateAsync(
        string username,
        string password,
        System.Threading.CancellationToken ct = default);
}

/// <summary>
/// A successfully authenticated SMTP submission user. Carried on
/// <see cref="DeliveryContext"/> after auth completes so the sink and
/// downstream routing can attribute the submission.
/// </summary>
public sealed class AuthenticatedUser
{
    /// <summary>The username under which the client authenticated.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Domains the user is permitted to send <c>MAIL FROM</c> as. If empty,
    /// the user can send from any domain (admin-level authority). If
    /// non-empty, <c>MAIL FROM:&lt;addr&gt;</c> is rejected unless
    /// <c>addr</c>'s domain is in this list.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> AllowedFromDomains { get; init; }
        = System.Array.Empty<string>();
}

/// <summary>
/// Pluggable resolver for "is this domain local to me?" The MTA port
/// (typically 25) uses this to refuse relaying mail for non-local
/// destinations - i.e., the open-relay guard.
/// </summary>
/// <summary>
/// Optional: whether this server has somewhere to put mail for a recipient
/// on one of its own domains - a mailbox, or a rule that routes it onward.
/// Used to refuse an unknown recipient at RCPT TO, before the message is
/// transferred, rather than after (DEF-042).
/// </summary>
public interface IRecipientResolver
{
    /// <summary>
    /// True when mail for this address can be delivered, false when it
    /// certainly cannot. Returns null when it cannot be determined - the
    /// caller then accepts the recipient, so no uncertainty ever becomes a
    /// refusal.
    /// </summary>
    /// <param name="address">The full recipient address.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<bool?> ExistsAsync(string address, System.Threading.CancellationToken ct = default);
}

public interface ILocalDomainResolver
{
    /// <summary>
    /// Check whether a domain is considered local (Anjal serves mail for
    /// it). Comparison should be case-insensitive.
    /// </summary>
    /// <param name="domain">Domain name to check (without angle brackets, lowercase preferred).</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<bool> IsLocalAsync(string domain, System.Threading.CancellationToken ct = default);
}

/// <summary>
/// The role an SMTP listener plays. Affects which commands are accepted
/// and which authorization checks apply.
/// </summary>
public enum SmtpServerRole
{
    /// <summary>
    /// Public-facing port 25 listener for inter-server mail delivery (MTA
    /// role). Accepts mail without authentication, but only for local
    /// domains (refuses relay). Does not advertise AUTH.
    /// </summary>
    Mta = 0,

    /// <summary>
    /// Submission port (typically 587) for authenticated client submission
    /// per RFC 6409. Requires AUTH before MAIL FROM (after STARTTLS unless
    /// explicitly opted into plaintext). Accepts mail for any destination.
    /// </summary>
    Submission = 1,
}
