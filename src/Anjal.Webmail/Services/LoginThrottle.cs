namespace Anjal.Webmail.Services;

/// <summary>
/// Limits webmail sign-in guessing on two keys at once.
/// <list type="bullet">
///   <item>Per client address and account: five failures in 15 minutes and
///   that pair is refused. Stops one machine guessing one password.</item>
///   <item>Per account from anywhere: fifty failures in 15 minutes. Bounds a
///   guess spread across many addresses to a few thousand a day, while being
///   high enough that locking a real user out takes deliberate effort.</item>
/// </list>
/// Keying on the account as well as the address matters for hospitals: many
/// staff share one outbound address behind NAT, so an address-only limit
/// strict enough to matter would lock out a whole building.
/// </summary>
public sealed class LoginThrottle
{
    private readonly Anjal.Smtp.AuthFailureLimiter perPair;
    private readonly Anjal.Smtp.AuthFailureLimiter perAccount;

    /// <summary>Construct with the default limits.</summary>
    public LoginThrottle()
        : this(5, 50, TimeSpan.FromMinutes(15), null)
    {
    }

    /// <summary>Construct.</summary>
    /// <param name="perPairFailures">Failures allowed for one address and account.</param>
    /// <param name="perAccountFailures">Failures allowed for one account from anywhere.</param>
    /// <param name="window">The sliding window.</param>
    /// <param name="clock">Clock, for tests.</param>
    public LoginThrottle(int perPairFailures, int perAccountFailures, TimeSpan window, Func<DateTimeOffset>? clock)
    {
        this.perPair = new Anjal.Smtp.AuthFailureLimiter(perPairFailures, window, clock);
        this.perAccount = new Anjal.Smtp.AuthFailureLimiter(perAccountFailures, window, clock);
    }

    /// <summary>Whether a sign-in may be attempted now.</summary>
    /// <param name="client">Client address.</param>
    /// <param name="account">The address being signed in to, lower-cased.</param>
    public bool IsAllowed(string client, string account)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);
        return this.perPair.IsAllowed(client + "|" + account) && this.perAccount.IsAllowed(account);
    }

    /// <summary>Record a failed sign-in.</summary>
    /// <param name="client">Client address.</param>
    /// <param name="account">The account.</param>
    public void RecordFailure(string client, string account)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);
        this.perPair.RecordFailure(client + "|" + account);
        this.perAccount.RecordFailure(account);
    }

    /// <summary>
    /// Record a successful sign-in. Kept for symmetry and future use; a
    /// success does not erase earlier failures, so an attacker who knows one
    /// valid password cannot use it to reset the counter for guessing others.
    /// </summary>
    /// <param name="client">Client address.</param>
    /// <param name="account">The account.</param>
    public void RecordSuccess(string client, string account)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);
    }
}
