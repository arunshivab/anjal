using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Anjal.Webmail.Services;

/// <summary>One signed-in device, as the mailbox's "sessions" document keeps it (rc.13).</summary>
/// <param name="Id">The session's id; the cookie carries it.</param>
/// <param name="Shared">True when signed in on a shared computer.</param>
/// <param name="Device">"Windows, Chrome" and the like.</param>
/// <param name="Phone">True for a phone or tablet.</param>
/// <param name="Address">The network address it signed in from.</param>
/// <param name="Started">When it signed in.</param>
/// <param name="LastSeen">When it was last used.</param>
public sealed record SessionRecord(string Id, bool Shared, string Device, bool Phone, string Address, DateTimeOffset Started, DateTimeOffset LastSeen);

/// <summary>What a session check found.</summary>
public enum SessionState
{
    /// <summary>Signed in.</summary>
    Valid = 0,

    /// <summary>Signed out for being idle too long.</summary>
    Idle = 1,

    /// <summary>Signed out: from another device, by a password reset, or before rc.13.</summary>
    Ended = 2,
}

/// <summary>A sign-in waiting for its second step (rc.13).</summary>
public sealed class PendingSignIn
{
    /// <summary>Its id; a short-lived cookie carries it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The mailbox.</summary>
    public Guid MailboxId { get; init; }

    /// <summary>The address, for the audit trail.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>True when on a shared computer.</summary>
    public bool Shared { get; init; }

    /// <summary>When it lapses: ten minutes after the password was accepted.</summary>
    public DateTimeOffset Expires { get; init; }

    /// <summary>Wrong second steps so far; five and it is abandoned.</summary>
    public int Attempts { get; set; }

    /// <summary>The approval asked of another device, if any.</summary>
    public string? ApprovalId { get; set; }
}

/// <summary>A request for another signed-in device to approve a sign-in or a reset (rc.13).</summary>
public sealed class Approval
{
    /// <summary>Its id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The mailbox.</summary>
    public Guid MailboxId { get; init; }

    /// <summary>"sign-in" or "reset".</summary>
    public string Kind { get; init; } = "sign-in";

    /// <summary>The device asking, for example "Windows, Chrome".</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>Its network address.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>When it was asked.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>When it lapses.</summary>
    public DateTimeOffset Expires { get; init; }

    /// <summary>Null while waiting; true approved; false refused.</summary>
    public bool? Answer { get; set; }

    /// <summary>For a reset: whether the new password has been set with it.</summary>
    public bool Used { get; set; }
}

/// <summary>
/// Signed-in devices (rc.13): each sign-in is a session with an id carried in
/// the cookie, so one can be signed out from another device, and a session
/// idle too long ends - 15 minutes on a shared computer, 8 hours on the
/// person's own, which also lasts at most 30 days. Also keeps, for a few
/// minutes, sign-ins waiting for their second step, requests for another
/// device's approval, and passkey challenges.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    /// <summary>The claim carrying the session's id.</summary>
    public const string SessionClaim = "anjal:sid";

    /// <summary>The claim saying the computer is shared ("1").</summary>
    public const string SharedClaim = "anjal:shared";

    /// <summary>Idle limit on a shared computer.</summary>
    public static readonly TimeSpan SharedIdle = TimeSpan.FromMinutes(15);

    /// <summary>Idle limit on the person's own device.</summary>
    public static readonly TimeSpan OwnIdle = TimeSpan.FromHours(8);

    /// <summary>The longest a session lasts on the person's own device.</summary>
    public static readonly TimeSpan OwnLife = TimeSpan.FromDays(30);

    /// <summary>The longest a session lasts on a shared computer.</summary>
    public static readonly TimeSpan SharedLife = TimeSpan.FromHours(12);

    /// <summary>How many signed-in devices a mailbox keeps; the oldest is signed out beyond that.</summary>
    public const int MaxSessions = 20;

    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    private readonly MailboxService mail;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingSignIn> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Approval> approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (byte[] Challenge, Guid? MailboxId, string Purpose, DateTimeOffset Expires)> challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Codes, DateTimeOffset Expires)> reveals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> proofs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Construct.</summary>
    /// <param name="mail">Where the list of devices is kept.</param>
    /// <param name="clock">The clock; tests move it.</param>
    public SessionRegistry(MailboxService mail, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(mail);
        this.mail = mail;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public void Dispose() => this.gate.Dispose();

    /// <summary>The idle limit for a kind of computer.</summary>
    /// <param name="shared">True for a shared computer.</param>
    /// <returns>15 minutes or 8 hours.</returns>
    public static TimeSpan IdleLimit(bool shared) => shared ? SharedIdle : OwnIdle;

    /// <summary>The session id a principal carries, or null.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>The id.</returns>
    public static string? SessionOf(ClaimsPrincipal? user) => user?.FindFirst(SessionClaim)?.Value;

    /// <summary>True when a principal signed in on a shared computer.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>Whether the computer is shared.</returns>
    public static bool IsShared(ClaimsPrincipal? user) => user?.FindFirst(SharedClaim)?.Value == "1";

    /// <summary>A new random id: 128 bits, base64url.</summary>
    /// <returns>The id.</returns>
    public static string NewId() => WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>Start a session: added to the mailbox's devices. Returns its id.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="shared">True on a shared computer.</param>
    /// <param name="userAgent">The browser's User-Agent.</param>
    /// <param name="address">The network address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The session id.</returns>
    public async Task<string> StartAsync(Guid mailboxId, bool shared, string? userAgent, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        string id = NewId();
        DateTimeOffset now = this.clock();
        var record = new SessionRecord(id, shared, DeviceName.Of(userAgent), DeviceName.IsPhone(userAgent), address, now, now);
        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<SessionRecord> all = await this.mail.GetSessionsAsync(mailboxId, ct).ConfigureAwait(false);
            all.RemoveAll(s => Expired(s, now));
            all.Add(record);
            while (all.Count > MaxSessions)
            {
                SessionRecord oldest = all.OrderBy(s => s.LastSeen).First();
                all.Remove(oldest);
                this.live.TryRemove(oldest.Id, out _);
            }
            await this.mail.SaveSessionsAsync(mailboxId, all, ct).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
        Live state = new Live(mailboxId, shared, now, now, now);
        await this.ApplyPolicyAsync(state, ct).ConfigureAwait(false);
        this.live[id] = state;
        return id;
    }

    /// <summary>
    /// Check a signed-in principal on a request. An idle or ended session is
    /// removed. <paramref name="activity"/> marks the person as active.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <param name="activity">True when the request is the person doing something, not the page checking in.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is still signed in.</returns>
    public async Task<SessionState> CheckAsync(ClaimsPrincipal user, bool activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? sid = SessionOf(user);
        Guid? mailboxId = WebmailAuthService.PersonIdOf(user);
        if (sid is null || mailboxId is null)
        {
            return SessionState.Ended;
        }
        DateTimeOffset now = this.clock();
        if (!this.live.TryGetValue(sid, out Live? state))
        {
            List<SessionRecord> all = await this.mail.GetSessionsAsync(mailboxId.Value, ct).ConfigureAwait(false);
            SessionRecord? record = all.Find(s => s.Id == sid);
            if (record is null)
            {
                return SessionState.Ended;
            }
            state = this.live.GetOrAdd(sid, _ => new Live(mailboxId.Value, record.Shared, record.Started, record.LastSeen, record.LastSeen));
        }
        if (state.MailboxId != mailboxId.Value)
        {
            return SessionState.Ended;
        }
        // rc.14: the organisation's own limits, never beyond Anjal's.
        await this.ApplyPolicyAsync(state, ct).ConfigureAwait(false);
        if (now - state.LastActive > state.IdleLimit || now - state.Started > state.Life)
        {
            await this.EndAsync(mailboxId.Value, sid, ct).ConfigureAwait(false);
            return SessionState.Idle;
        }
        if (activity)
        {
            state.LastActive = now;
            if (now - state.LastSaved > SaveEvery)
            {
                state.LastSaved = now;
                await this.SaveSeenAsync(mailboxId.Value, sid, now, ct).ConfigureAwait(false);
            }
        }
        return SessionState.Valid;
    }

    /// <summary>Seconds until a session is signed out for being idle, or null when it is not signed in.</summary>
    /// <param name="sid">The session.</param>
    /// <returns>Seconds left.</returns>
    public int? SecondsLeft(string? sid)
    {
        if (sid is null || !this.live.TryGetValue(sid, out Live? state))
        {
            return null;
        }
        TimeSpan left = state.IdleLimit - (this.clock() - state.LastActive);
        return (int)Math.Max(0, Math.Ceiling(left.TotalSeconds));
    }

    /// <summary>Sign one session out.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="sid">The session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> EndAsync(Guid mailboxId, string sid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sid);
        this.live.TryRemove(sid, out _);
        return await this.RemoveAsync(mailboxId, s => s.Id == sid, ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Sign out every session of a mailbox but one (Sign out everywhere else).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="keep">The session to keep, or null to sign out all (after a password reset).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were signed out.</returns>
    public async Task<int> EndOthersAsync(Guid mailboxId, string? keep, CancellationToken ct = default)
    {
        foreach (KeyValuePair<string, Live> kv in this.live)
        {
            if (kv.Value.MailboxId == mailboxId && kv.Key != keep)
            {
                this.live.TryRemove(kv.Key, out _);
            }
        }
        return await this.RemoveAsync(mailboxId, s => s.Id != keep, ct).ConfigureAwait(false);
    }

    /// <summary>The signed-in devices, the current one first, then the most recently used.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="current">The session asking.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<IReadOnlyList<SessionRecord>> ListAsync(Guid mailboxId, string? current, CancellationToken ct = default)
    {
        DateTimeOffset now = this.clock();
        List<SessionRecord> all = await this.mail.GetSessionsAsync(mailboxId, ct).ConfigureAwait(false);
        return all
            .Where(s => !Expired(s, now))
            .Select(s => this.live.TryGetValue(s.Id, out Live? l) && l.LastActive > s.LastSeen ? s with { LastSeen = l.LastActive } : s)
            .OrderByDescending(s => s.Id == current)
            .ThenByDescending(s => s.LastSeen)
            .ToList();
    }

    /// <summary>Keep a sign-in that is waiting for its second step. Returns its id.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="address">Its address.</param>
    /// <param name="shared">True on a shared computer.</param>
    /// <returns>The id for the waiting cookie.</returns>
    public string AddPending(Guid mailboxId, string address, bool shared)
    {
        this.Sweep();
        string id = NewId();
        this.pending[id] = new PendingSignIn { Id = id, MailboxId = mailboxId, Address = address, Shared = shared, Expires = this.clock() + TimeSpan.FromMinutes(10) };
        return id;
    }

    /// <summary>A sign-in waiting for its second step, while it lasts.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The waiting sign-in, or null.</returns>
    public PendingSignIn? Pending(string? id) =>
        id is not null && this.pending.TryGetValue(id, out PendingSignIn? p) && p.Expires > this.clock() && p.Attempts < 5 ? p : null;

    /// <summary>Forget a waiting sign-in (finished or abandoned).</summary>
    /// <param name="id">Its id.</param>
    public void RemovePending(string? id)
    {
        if (id is not null)
        {
            this.pending.TryRemove(id, out _);
        }
    }

    /// <summary>Ask the mailbox's other signed-in devices to approve something.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="kind">"sign-in" or "reset".</param>
    /// <param name="userAgent">The asking browser.</param>
    /// <param name="address">Its network address.</param>
    /// <returns>The request.</returns>
    public Approval RequestApproval(Guid mailboxId, string kind, string? userAgent, string address)
    {
        this.Sweep();
        DateTimeOffset now = this.clock();
        var approval = new Approval { Id = NewId(), MailboxId = mailboxId, Kind = kind, Device = DeviceName.Of(userAgent), Address = address, At = now, Expires = now + TimeSpan.FromMinutes(5) };
        this.approvals[approval.Id] = approval;
        return approval;
    }

    /// <summary>An approval request, while it lasts.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The request, or null.</returns>
    public Approval? ApprovalOf(string? id) =>
        id is not null && this.approvals.TryGetValue(id, out Approval? a) && a.Expires > this.clock() ? a : null;

    /// <summary>The requests waiting for a mailbox's answer, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The requests.</returns>
    public IReadOnlyList<Approval> Waiting(Guid mailboxId)
    {
        DateTimeOffset now = this.clock();
        return this.approvals.Values.Where(a => a.MailboxId == mailboxId && a.Answer is null && a.Expires > now).OrderByDescending(a => a.At).ToList();
    }

    /// <summary>Answer a request from a signed-in device of the same mailbox.</summary>
    /// <param name="mailboxId">The mailbox answering.</param>
    /// <param name="id">The request.</param>
    /// <param name="approve">True to approve.</param>
    /// <returns>Whether there was such a request.</returns>
    public bool Answer(Guid mailboxId, string id, bool approve)
    {
        Approval? a = this.ApprovalOf(id);
        if (a is null || a.MailboxId != mailboxId || a.Answer is not null)
        {
            return false;
        }
        a.Answer = approve;
        return true;
    }

    /// <summary>A new passkey challenge, used once within five minutes. Returns its id and bytes.</summary>
    /// <param name="mailboxId">The mailbox it is for, when known.</param>
    /// <param name="purpose">"register" or "sign-in".</param>
    /// <returns>The id and the challenge.</returns>
    public (string Id, byte[] Challenge) NewChallenge(Guid? mailboxId, string purpose)
    {
        this.Sweep();
        string id = NewId();
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        this.challenges[id] = (challenge, mailboxId, purpose, this.clock() + TimeSpan.FromMinutes(5));
        return (id, challenge);
    }

    /// <summary>Take a challenge back - once - when it matches its purpose and mailbox.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="purpose">The purpose.</param>
    /// <returns>The challenge, or null.</returns>
    public byte[]? TakeChallenge(string? id, Guid mailboxId, string purpose)
    {
        if (id is null || !this.challenges.TryRemove(id, out (byte[] Challenge, Guid? MailboxId, string Purpose, DateTimeOffset Expires) c))
        {
            return null;
        }
        return c.Expires > this.clock() && c.Purpose == purpose && c.MailboxId == mailboxId ? c.Challenge : null;
    }

    /// <summary>Hold new backup codes for the session's next page, which shows them once.</summary>
    /// <param name="sid">The session.</param>
    /// <param name="codes">The codes.</param>
    public void Reveal(string sid, IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(sid);
        ArgumentNullException.ThrowIfNull(codes);
        this.reveals[sid] = (codes, this.clock() + TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// How long a password or passkey just given lets a person make security changes without
    /// giving it again (DES-11 S4, owner 10 Oct 2026: "A").
    /// </summary>
    public static readonly TimeSpan ProofLife = TimeSpan.FromMinutes(5);

    /// <summary>Note that the person of a session has just given their password or passkey.</summary>
    /// <param name="sid">The session.</param>
    public void MarkProved(string? sid)
    {
        if (!string.IsNullOrEmpty(sid))
        {
            this.proofs[sid] = this.clock();
        }
    }

    /// <summary>True when the person of a session gave their password or passkey within <see cref="ProofLife"/>.</summary>
    /// <param name="sid">The session.</param>
    /// <returns>Whether a security change may go ahead without asking again.</returns>
    public bool ProvedRecently(string? sid) =>
        sid is not null && this.proofs.TryGetValue(sid, out DateTimeOffset at) && this.clock() - at <= ProofLife;

    /// <summary>Take the codes held for a session - once.</summary>
    /// <param name="sid">The session.</param>
    /// <returns>The codes, or null.</returns>
    public IReadOnlyList<string>? TakeReveal(string? sid) =>
        sid is not null && this.reveals.TryRemove(sid, out (IReadOnlyList<string> Codes, DateTimeOffset Expires) r) && r.Expires > this.clock() ? r.Codes : null;

    private async Task ApplyPolicyAsync(Live state, CancellationToken ct)
    {
        SignInPolicy policy = await this.mail.SignInPolicyForAsync(state.MailboxId, ct).ConfigureAwait(false);
        // The rules were checked against Anjal's limits when they were saved.
        state.IdleLimit = policy.Idle(state.Shared);
        state.Life = state.Shared ? SharedLife : TimeSpan.FromDays(Math.Min(policy.StayDays, OwnLife.TotalDays));
    }

    private static bool Expired(SessionRecord s, DateTimeOffset now) =>
        now - s.LastSeen > IdleLimit(s.Shared) + SaveEvery || now - s.Started > (s.Shared ? SharedLife : OwnLife);

    private async Task SaveSeenAsync(Guid mailboxId, string sid, DateTimeOffset now, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<SessionRecord> all = await this.mail.GetSessionsAsync(mailboxId, ct).ConfigureAwait(false);
            int i = all.FindIndex(s => s.Id == sid);
            if (i >= 0)
            {
                all[i] = all[i] with { LastSeen = now };
                await this.mail.SaveSessionsAsync(mailboxId, all, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<int> RemoveAsync(Guid mailboxId, Predicate<SessionRecord> which, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<SessionRecord> all = await this.mail.GetSessionsAsync(mailboxId, ct).ConfigureAwait(false);
            int removed = all.RemoveAll(which);
            if (removed > 0)
            {
                await this.mail.SaveSessionsAsync(mailboxId, all, ct).ConfigureAwait(false);
            }
            return removed;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private void Sweep()
    {
        DateTimeOffset now = this.clock();
        foreach (KeyValuePair<string, PendingSignIn> kv in this.pending)
        {
            if (kv.Value.Expires < now)
            {
                this.pending.TryRemove(kv.Key, out _);
            }
        }
        foreach (KeyValuePair<string, Approval> kv in this.approvals)
        {
            if (kv.Value.Expires < now)
            {
                this.approvals.TryRemove(kv.Key, out _);
            }
        }
        foreach (KeyValuePair<string, (byte[] Challenge, Guid? MailboxId, string Purpose, DateTimeOffset Expires)> kv in this.challenges)
        {
            if (kv.Value.Expires < now)
            {
                this.challenges.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed class Live
    {
        public Live(Guid mailboxId, bool shared, DateTimeOffset started, DateTimeOffset lastActive, DateTimeOffset lastSaved)
        {
            this.MailboxId = mailboxId;
            this.Shared = shared;
            this.Started = started;
            this.LastActive = lastActive;
            this.LastSaved = lastSaved;
        }

        public Guid MailboxId { get; }

        public bool Shared { get; }

        public DateTimeOffset Started { get; }

        public TimeSpan IdleLimit { get; set; } = SharedIdle;

        public TimeSpan Life { get; set; } = SharedLife;

        // Read and written from many requests at once: kept as ticks, changed atomically.
        private long lastActive;
        private long lastSaved;

        public DateTimeOffset LastActive
        {
            get => new(Interlocked.Read(ref this.lastActive), TimeSpan.Zero);
            set => Interlocked.Exchange(ref this.lastActive, value.UtcTicks);
        }

        public DateTimeOffset LastSaved
        {
            get => new(Interlocked.Read(ref this.lastSaved), TimeSpan.Zero);
            set => Interlocked.Exchange(ref this.lastSaved, value.UtcTicks);
        }
    }
}
