namespace Anjal.Smtp;

/// <summary>
/// Decision returned by an <see cref="ISmtpPolicy"/> check.
/// </summary>
public sealed class PolicyDecision
{
    /// <summary>A decision that lets the command proceed.</summary>
    public static readonly PolicyDecision Allow = new() { Allowed = true };

    /// <summary>Whether the command may proceed.</summary>
    public bool Allowed { get; init; }

    /// <summary>
    /// SMTP reply code to send when not allowed. 4xx asks the client to
    /// retry later (rate limits, greylisting); 5xx refuses permanently.
    /// </summary>
    public int ReplyCode { get; init; } = 451;

    /// <summary>Reply text to send when not allowed.</summary>
    public string ReplyText { get; init; } = "Try again later";

    /// <summary>Build a temporary (4xx) refusal.</summary>
    /// <param name="text">Reply text.</param>
    /// <param name="code">Reply code, default 451.</param>
    public static PolicyDecision Defer(string text, int code = 451) => new()
    {
        Allowed = false,
        ReplyCode = code,
        ReplyText = text,
    };

    /// <summary>Build a permanent (5xx) refusal.</summary>
    /// <param name="text">Reply text.</param>
    /// <param name="code">Reply code, default 550.</param>
    public static PolicyDecision Reject(string text, int code = 550) => new()
    {
        Allowed = false,
        ReplyCode = code,
        ReplyText = text,
    };
}

/// <summary>
/// Connection-level and transaction-level policy consulted by
/// <see cref="SmtpSession"/> before it acts on a command. Used for rate
/// limiting, greylisting and mailbox quota. Implementations must be
/// thread-safe: one instance serves every concurrent session of a
/// listener. Any exception thrown is treated as "allow" so a policy bug
/// can never take the server down. Methods are asynchronous so a policy
/// may consult the store (quota) as well as in-memory state.
/// </summary>
public interface ISmtpPolicy
{
    /// <summary>
    /// Called when a client connects, before the banner. A refusal closes
    /// the connection after sending the reply.
    /// </summary>
    /// <param name="remoteAddress">Client IP in dotted/colon form.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<PolicyDecision> OnConnectAsync(string remoteAddress, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Called on MAIL FROM after syntax and authorization checks.
    /// </summary>
    /// <param name="remoteAddress">Client IP.</param>
    /// <param name="authenticatedUser">Authenticated username on the submission port, or null.</param>
    /// <param name="envelopeFrom">The MAIL FROM address.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Called on RCPT TO after the relay check, once per recipient.
    /// </summary>
    /// <param name="remoteAddress">Client IP.</param>
    /// <param name="authenticatedUser">Authenticated username, or null.</param>
    /// <param name="envelopeFrom">The MAIL FROM address.</param>
    /// <param name="recipient">The RCPT TO address.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, System.Threading.CancellationToken ct = default);
}
