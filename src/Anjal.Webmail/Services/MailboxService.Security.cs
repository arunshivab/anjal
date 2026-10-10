using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Anjal.Mime;
using Anjal.Store;
using Microsoft.AspNetCore.DataProtection;

namespace Anjal.Webmail.Services;

/// <summary>A device trusted to skip the second step for thirty days (rc.13).</summary>
/// <param name="Hash">SHA-256 of the device's token, hex.</param>
/// <param name="Until">When the trust ends.</param>
/// <param name="Name">The device, for the list.</param>
public sealed record TrustedDevice(string Hash, DateTimeOffset Until, string Name);

/// <summary>A password reset in progress (rc.13): a six-digit code sent to the recovery address.</summary>
public sealed class ResetState
{
    /// <summary>SHA-256 of the mailbox id and the code, hex.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>When the code stops working: ten minutes after it was sent.</summary>
    public DateTimeOffset Expires { get; set; }

    /// <summary>Wrong codes typed so far; five and the code is spent.</summary>
    public int Attempts { get; set; }

    /// <summary>When it was sent; a new one may be asked for a minute later.</summary>
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>An invitation waiting to be accepted (rc.13).</summary>
public sealed class InvitationState
{
    /// <summary>SHA-256 of the link's secret, hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>When the link stops working: seven days after it was made.</summary>
    public DateTimeOffset Expires { get; set; }

    /// <summary>Who sent it, by name.</summary>
    public string InvitedBy { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; set; }
}

/// <summary>
/// A mailbox's sign-in security (rc.13), kept as the mailbox's "security"
/// document: the recovery address, the authenticator secret (encrypted),
/// backup codes (hashed), passkeys, earlier passwords (hashed), trusted
/// devices, and any reset or invitation in progress.
/// </summary>
public sealed class SecurityDocument
{
    /// <summary>Where reset codes go; empty when none.</summary>
    public string RecoveryAddress { get; set; } = string.Empty;

    /// <summary>The authenticator secret, encrypted; empty when the app is not set up.</summary>
    public string TotpSecret { get; set; } = string.Empty;

    /// <summary>The step of the last code accepted, so no code works twice.</summary>
    public long TotpLastStep { get; set; }

    /// <summary>A secret being set up, encrypted, until its first code is typed.</summary>
    public string PendingTotpSecret { get; set; } = string.Empty;

    /// <summary>SHA-256 of each unused backup code, hex.</summary>
    public List<string> BackupCodes { get; set; } = new();

    /// <summary>When the backup codes were made.</summary>
    public DateTimeOffset? BackupCodesMade { get; set; }

    /// <summary>Passkeys.</summary>
    public List<PasskeyRecord> Passkeys { get; set; } = new();

    /// <summary>
    /// The hashes of earlier passwords, as many as the organisation refuses (owner, 10 Oct 2026, P8:
    /// none by default, the last 3 or 5 when the organisation asks), the current one not included.
    /// </summary>
    public List<string> PasswordHistory { get; set; } = new();

    /// <summary>When the password was last changed here.</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Devices that skip the second step.</summary>
    public List<TrustedDevice> TrustedDevices { get; set; } = new();

    /// <summary>A reset in progress, or null.</summary>
    public ResetState? Reset { get; set; }

    /// <summary>An invitation waiting, or null.</summary>
    public InvitationState? Invitation { get; set; }

    /// <summary>True when the organisation asks for a new password before anything else (rc.14).</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Until when the second step is refused after five wrong codes (DES-11 S3); null when not.</summary>
    public DateTimeOffset? SecondStepLockedUntil { get; set; }

    /// <summary>Wrong passwords typed in a row at sign-in, since the last right one (owner, 10 Oct 2026, P6).</summary>
    public int WrongPasswords { get; set; }

    /// <summary>Until when signing in with the password is paused after ten wrong ones in a row (P6); null when not.</summary>
    public DateTimeOffset? PasswordPausedUntil { get; set; }

    /// <summary>
    /// True when the password, as last typed at sign-in, is shorter than the rules now ask for this
    /// person (owner, 10 Oct 2026: 15 characters alone, 12 with two-step); a new one is asked for.
    /// </summary>
    public bool PasswordBelowRules { get; set; }

    /// <summary>The sign-in before the latest, for the status bar's "Last sign-in" (DES-11 S5).</summary>
    public SignInMark? PreviousSignIn { get; set; }

    /// <summary>The latest sign-in.</summary>
    public SignInMark? LatestSignIn { get; set; }

    /// <summary>The devices (browser and system) this person has signed in from, to tell a new one (DES-11 S5).</summary>
    public List<string> SeenDevices { get; set; } = new();

    /// <summary>True when signing in asks for a second step: an authenticator app or a passkey.</summary>
    public bool TwoStepOn => this.TotpSecret.Length > 0 || this.Passkeys.Count > 0;
}

/// <summary>One sign-in, as the status bar shows it (DES-11 S5).</summary>
/// <param name="At">When.</param>
/// <param name="Place">Town and country, or empty when not known.</param>
/// <param name="Device">Browser and system, for example "Windows, Chrome".</param>
public sealed record SignInMark(DateTimeOffset At, string Place, string Device);

/// <summary>The organisation a sign-in page belongs to (rc.13).</summary>
/// <param name="TenantId">The organisation.</param>
/// <param name="Name">Its name, for "[Organisation] mail".</param>
/// <param name="Domain">The domain a user name is completed with.</param>
/// <param name="Logo">Its logo as a data address, or empty.</param>
/// <param name="Title">What the sign-in page calls it, for example "Imagiqa mail" (rc.14).</param>
/// <param name="Message">One line under the sign-in form, or empty (rc.14).</param>
public sealed record OrganisationIdentity(Guid TenantId, string Name, string Domain, string Logo, string Title = "", string Message = "");

/// <summary>
/// Accounts and sign-in security (rc.13, boards AuthSignIn, AuthReset,
/// AuthInvite, AuthTwoStep and SetSecurity): earlier passwords, the recovery
/// address and reset codes, invitations, the authenticator app, backup codes,
/// passkeys and trusted devices.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document holding a mailbox's sign-in security.</summary>
    public const string SecurityKind = "security";

    /// <summary>The kind of the document listing a mailbox's signed-in devices.</summary>
    public const string SessionsKind = "sessions";

    /// <summary>The kind of an organisation's document holding its sign-in look.</summary>
    public const string BrandingKind = "branding";

    /// <summary>Wrong passwords in a row before signing in with the password pauses (owner, 10 Oct 2026, P6; PCI DSS 4.0 8.3.4).</summary>
    public const int WrongPasswordLimit = 10;

    /// <summary>How long signing in with the password pauses after <see cref="WrongPasswordLimit"/> wrong ones.</summary>
    public static readonly TimeSpan PasswordPause = TimeSpan.FromMinutes(30);

    /// <summary>How long a reset code works.</summary>
    public static readonly TimeSpan ResetCodeLife = TimeSpan.FromMinutes(10);

    /// <summary>How long an invitation works.</summary>
    public static readonly TimeSpan InvitationLife = TimeSpan.FromDays(7);

    /// <summary>How long a trusted device skips the second step.</summary>
    public static readonly TimeSpan TrustLife = TimeSpan.FromDays(30);

    /// <summary>How many backup codes are made at a time.</summary>
    public const int BackupCodeCount = 10;

    // Backup codes avoid letters and digits that are easily confused (0/o, 1/l/i).
    private const string BackupAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private static readonly IDataProtector FallbackProtector = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider().CreateProtector("anjal.secrets.v1");

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, OrganisationIdentity? Org)> organisations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Encrypts the authenticator secrets kept in the database. Null uses a key that lasts only while the process runs (tests).</summary>
    public IDataProtector? Protector { get; init; }

    private IDataProtector Secrets => this.Protector ?? FallbackProtector;

    /// <summary>A mailbox's sign-in security.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document; empty when none has been saved.</returns>
    public async Task<SecurityDocument> GetSecurityAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<SecurityDocument>(mailboxId, SecurityKind, ct).ConfigureAwait(false) ?? new SecurityDocument();

    /// <summary>The recovery address with most of its name hidden: a••••@gmail.com.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The masked address, or empty.</returns>
    public static string MaskAddress(string? address)
    {
        string a = (address ?? string.Empty).Trim();
        int at = a.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? string.Empty : a[0] + "••••" + a[at..];
    }

    /// <summary>
    /// The mailbox a sign-in names: a full address, or a user name completed
    /// with the organisation's domain. Null when there is none.
    /// </summary>
    /// <param name="typed">What was typed.</param>
    /// <param name="domain">The organisation's domain, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailbox, or null.</returns>
    public async Task<MailboxRow?> FindMailboxAsync(string typed, string? domain, CancellationToken ct = default)
    {
        string address = FullAddress(typed, domain);
        return Anjal.Mailbox.MailboxSink.TrySplitAddress(address, out string local, out string d)
            ? await this.store.GetMailboxAsync(local, d, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>A user name completed with a domain: "arun" and "example.in" make "arun@example.in".</summary>
    /// <param name="typed">A user name or a full address.</param>
    /// <param name="domain">The domain, or null.</param>
    /// <returns>The address, lower-cased.</returns>
    public static string FullAddress(string? typed, string? domain)
    {
        string t = (typed ?? string.Empty).Trim().ToLowerInvariant();
        return t.Contains('@', StringComparison.Ordinal) || string.IsNullOrEmpty(domain) ? t : t + "@" + domain.ToLowerInvariant();
    }

    /// <summary>
    /// The organisation a host name belongs to (the sign-in page's "[Organisation] mail"):
    /// mail.example.in belongs to whoever has example.in; when the server has one
    /// organisation's domains only, that one. Kept for a minute.
    /// </summary>
    /// <param name="host">The host the page was asked for.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation, or null when it cannot be told.</returns>
    public async Task<OrganisationIdentity?> OrganisationForHostAsync(string? host, CancellationToken ct = default)
    {
        string h = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (this.organisations.TryGetValue(h, out (DateTimeOffset At, OrganisationIdentity? Org) cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1))
        {
            return cached.Org;
        }
        IReadOnlyList<TenantDomainRow> domains;
        try
        {
            domains = await this.store.ListTenantDomainsAsync(null, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The sign-in page must still show when the store is down (DEF-040); signing in then explains.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
#pragma warning restore CA1031
        TenantDomainRow? match = null;
        for (string candidate = h; candidate.Length > 0 && match is null;)
        {
            match = domains.FirstOrDefault(d => string.Equals(d.Domain, candidate, StringComparison.OrdinalIgnoreCase));
            int dot = candidate.IndexOf('.', StringComparison.Ordinal);
            candidate = dot < 0 ? string.Empty : candidate[(dot + 1)..];
        }
        if (match is null && domains.Select(d => d.TenantId).Distinct().Count() == 1)
        {
            match = domains.OrderBy(d => d.CreatedAt).First();
        }
        OrganisationIdentity? org = null;
        if (match is not null && await this.store.GetTenantByIdAsync(match.TenantId, ct).ConfigureAwait(false) is TenantRow tenant && tenant.Enabled)
        {
            Branding branding = await this.ReadTenantDocumentAsync<Branding>(tenant.Id, BrandingKind, ct).ConfigureAwait(false) ?? new Branding();
            string logo = branding.Logo.StartsWith("data:image/", StringComparison.Ordinal) ? branding.Logo : string.Empty;
            string name = string.IsNullOrWhiteSpace(tenant.DisplayName) ? tenant.Slug : tenant.DisplayName.Trim();
            org = new OrganisationIdentity(tenant.Id, name, match.Domain.ToLowerInvariant(), logo, branding.Title.Trim(), branding.Message.Trim());
        }
        this.organisations[h] = (DateTimeOffset.UtcNow, org);
        return org;
    }

    /// <summary>
    /// Change the password after checking the current one (Settings, Security).
    /// Returns the error to show, or null on success.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="confirmPassword">The new password again; null when the page asks only once.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> ChangePasswordAsync(Guid mailboxId, string currentPassword, string newPassword, string? confirmPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        MailboxRow mailbox = context.Value.Mailbox;
        if (mailbox.PasswordPbkdf2.Length == 0 || !Anjal.Smtp.Pbkdf2Hasher.Verify(currentPassword, mailbox.PasswordPbkdf2))
        {
            return "The current password is not correct.";
        }
        if (confirmPassword is not null && !string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return "The two new passwords do not match.";
        }
        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            return "The new password is the same as the current one.";
        }
        return await this.SetPasswordAsync(mailbox, newPassword, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Set a new password once the person is known (a change, a reset, an
    /// invitation): the rules, then the earlier passwords when the organisation
    /// refuses them. Returns the error, or null.
    /// </summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> SetPasswordAsync(MailboxRow mailbox, string newPassword, CancellationToken ct = default) => this.SetPasswordAsync(mailbox, newPassword, null, ct);

    /// <summary>Set a new password, with the shortest length given (a reset asks the same of everyone; see <see cref="ResetLength"/>).</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="minimumLength">The shortest length; null for the person's own (<see cref="PasswordLengthForAsync"/>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetPasswordAsync(MailboxRow mailbox, string newPassword, int? minimumLength, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(newPassword);
        SignInPolicy policy = await this.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        int least = minimumLength ?? await this.PasswordLengthForAsync(mailbox, doc, ct).ConfigureAwait(false);
        if (await Anjal.Smtp.PasswordPolicy.CheckAsync(newPassword, mailbox.Address, mailbox.DisplayName, least, ct).ConfigureAwait(false) is string rejected)
        {
            return rejected;
        }
        int depth = policy.PasswordHistory;
        if (depth > 0 && UsedBefore(mailbox, doc, newPassword, depth))
        {
            return $"You have used that password before. Choose one different from your last {depth}.";
        }
        // Earlier passwords are kept only as far as the organisation refuses them (P8): none by default.
        if (mailbox.PasswordPbkdf2.Length > 0 && depth > 1)
        {
            doc.PasswordHistory.Insert(0, mailbox.PasswordPbkdf2);
        }
        int keep = Math.Max(0, depth - 1);
        if (doc.PasswordHistory.Count > keep)
        {
            doc.PasswordHistory.RemoveRange(keep, doc.PasswordHistory.Count - keep);
        }
        // DES-11 S2 (owner, 10 Oct 2026, "A"): a new password also forgets every trusted device, so
        // each must pass the second step once more - whoever knew the old one is out entirely.
        if (mailbox.PasswordPbkdf2.Length > 0)
        {
            doc.TrustedDevices.Clear();
        }
        doc.PasswordChangedAt = DateTimeOffset.UtcNow;
        doc.Reset = null;
        doc.MustChangePassword = false;
        doc.PasswordBelowRules = false;
        doc.WrongPasswords = 0;
        doc.PasswordPausedUntil = null;
        mailbox.PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash(newPassword);
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Set or clear the recovery address, after the password is confirmed. Returns the error, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="password">The current password.</param>
    /// <param name="address">The new address; empty clears it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetRecoveryAddressAsync(Guid mailboxId, string password, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(address);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password, context.Value.Mailbox.PasswordPbkdf2))
        {
            return "The password is not correct.";
        }
        string a = address.Trim();
        if (a.Length > 0 && RecoveryProblem(a, context.Value.Mailbox.Address) is string bad)
        {
            return bad;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        doc.RecoveryAddress = a.ToLowerInvariant();
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>What is wrong with a recovery address, or null.</summary>
    /// <param name="address">The address.</param>
    /// <param name="own">The mailbox's own address, which cannot be its own recovery.</param>
    /// <returns>A sentence, or null.</returns>
    public static string? RecoveryProblem(string address, string own)
    {
        ArgumentNullException.ThrowIfNull(address);
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(address);
        if (parsed.Count != 1 || !Anjal.Mailbox.MailboxSink.TrySplitAddress(parsed[0].Address, out _, out _))
        {
            return "That is not an email address.";
        }
        return string.Equals(parsed[0].Address, own, StringComparison.OrdinalIgnoreCase)
            ? "Choose an address other than this mailbox: a reset code sent here could not be read without signing in."
            : null;
    }

    /// <summary>
    /// Start setting up an authenticator app: a new secret, kept until its first
    /// code is typed. Returns the link for the picture and the secret to type.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string Uri, string Secret)?> BeginAuthenticatorAsync(Guid mailboxId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        byte[] secret = doc.PendingTotpSecret.Length > 0 ? this.Unprotect(doc.PendingTotpSecret) ?? Totp.NewSecret() : Totp.NewSecret();
        doc.PendingTotpSecret = this.Secrets.Protect(Base32.Encode(secret));
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        string org = string.IsNullOrWhiteSpace(context.Value.Tenant.DisplayName) ? context.Value.Tenant.Slug : context.Value.Tenant.DisplayName.Trim();
        return (Totp.Uri(org + " mail", context.Value.Mailbox.Address, secret), Base32.Grouped(secret));
    }

    /// <summary>
    /// Finish setting up the authenticator app with its first code. Returns
    /// the error, or the new backup codes when there were none (shown once).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="code">The code the app shows.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, IReadOnlyList<string> BackupCodes)> ConfirmAuthenticatorAsync(Guid mailboxId, string code, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        byte[]? secret = doc.PendingTotpSecret.Length > 0 ? this.Unprotect(doc.PendingTotpSecret) : null;
        if (secret is null)
        {
            return ("Start again: the set-up had expired.", Array.Empty<string>());
        }
        long? step = Totp.Verify(secret, code, DateTimeOffset.UtcNow, 0);
        if (step is null)
        {
            return ("That code did not match. Check the phone's time is right, and type the code the app shows now.", Array.Empty<string>());
        }
        doc.TotpSecret = doc.PendingTotpSecret;
        doc.PendingTotpSecret = string.Empty;
        doc.TotpLastStep = step.Value;
        IReadOnlyList<string> codes = Array.Empty<string>();
        if (doc.BackupCodes.Count == 0)
        {
            codes = NewCodes(doc);
        }
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return (null, codes);
    }

    /// <summary>Turn the authenticator app off, after the password is confirmed. Returns the error, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="password">The current password.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> RemoveAuthenticatorAsync(Guid mailboxId, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null || !Anjal.Smtp.Pbkdf2Hasher.Verify(password, mailbox.PasswordPbkdf2))
        {
            return "The password is not correct.";
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        doc.TotpSecret = string.Empty;
        doc.PendingTotpSecret = string.Empty;
        if (!doc.TwoStepOn)
        {
            doc.BackupCodes.Clear();
            doc.BackupCodesMade = null;
            doc.TrustedDevices.Clear();
        }
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Make a fresh set of backup codes; the old ones stop working. Shown once.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The codes.</returns>
    public async Task<IReadOnlyList<string>> NewBackupCodesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        IReadOnlyList<string> codes = NewCodes(doc);
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return codes;
    }

    /// <summary>Check a code from the authenticator app; it cannot be used again.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="code">The code.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was right.</returns>
    public async Task<bool> CheckAuthenticatorCodeAsync(Guid mailboxId, string code, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        byte[]? secret = doc.TotpSecret.Length > 0 ? this.Unprotect(doc.TotpSecret) : null;
        long? step = secret is null ? null : Totp.Verify(secret, code, DateTimeOffset.UtcNow, doc.TotpLastStep);
        if (step is null)
        {
            return false;
        }
        doc.TotpLastStep = step.Value;
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Use a backup code: right once, then gone.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="code">The code; spaces, dashes and case ignored.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was right.</returns>
    public async Task<bool> UseBackupCodeAsync(Guid mailboxId, string code, CancellationToken ct = default)
    {
        if ((await this.SignInPolicyForAsync(mailboxId, ct).ConfigureAwait(false)).Methods == "no-backup")
        {
            return false;
        }
        string hash = BackupHash(code);
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        int index = doc.BackupCodes.FindIndex(h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (index < 0)
        {
            return false;
        }
        doc.BackupCodes.RemoveAt(index);
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Keep a new passkey. Returns the error, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="passkey">The passkey.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AddPasskeyAsync(Guid mailboxId, PasskeyRecord passkey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        if (doc.Passkeys.Count >= 20)
        {
            return "You have the most passkeys allowed. Remove one first.";
        }
        if (doc.Passkeys.Any(p => p.Id == passkey.Id))
        {
            return "That passkey is already added.";
        }
        doc.Passkeys.Add(passkey);
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a passkey.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="id">Its id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether one was removed.</returns>
    public async Task<bool> RemovePasskeyAsync(Guid mailboxId, string id, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        int removed = doc.Passkeys.RemoveAll(p => p.Id == id);
        if (removed > 0)
        {
            if (!doc.TwoStepOn)
            {
                doc.TrustedDevices.Clear();
            }
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>Record that a passkey was used, with its new counter.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="id">Its id.</param>
    /// <param name="signCount">The counter.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task PasskeyUsedAsync(Guid mailboxId, string id, uint signCount, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        int i = doc.Passkeys.FindIndex(p => p.Id == id);
        if (i >= 0)
        {
            doc.Passkeys[i] = doc.Passkeys[i] with { SignCount = signCount, LastUsed = DateTimeOffset.UtcNow };
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Trust this device for thirty days. Returns the token for its cookie.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The device.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> TrustDeviceAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        string token = WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        doc.TrustedDevices.RemoveAll(d => d.Until < now);
        int trustDays = (await this.SignInPolicyForAsync(mailboxId, ct).ConfigureAwait(false)).TrustDays;
        doc.TrustedDevices.Add(new TrustedDevice(Sha256Hex(token), now + TimeSpan.FromDays(Math.Min(trustDays, TrustLife.TotalDays)), name));
        if (doc.TrustedDevices.Count > 20)
        {
            doc.TrustedDevices.RemoveAt(0);
        }
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return token;
    }

    /// <summary>True when a device's token is still trusted.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="token">The token from its cookie.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether to skip the second step.</returns>
    public async Task<bool> IsTrustedDeviceAsync(Guid mailboxId, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        string hash = Sha256Hex(token);
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        // rc.14: when the organisation has since shortened trust (or ended it),
        // a device counts only while its remaining time fits the new rule.
        TimeSpan allowed = TimeSpan.FromDays((await this.SignInPolicyForAsync(mailboxId, ct).ConfigureAwait(false)).TrustDays);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return doc.TrustedDevices.Any(d => d.Until > now && d.Until - now <= allowed + TimeSpan.FromMinutes(5)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.Hash), Encoding.ASCII.GetBytes(hash)));
    }

    /// <summary>Forget every trusted device (Sign out everywhere else).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task ForgetTrustedDevicesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        if (doc.TrustedDevices.Count > 0)
        {
            doc.TrustedDevices.Clear();
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send a six-digit reset code to the recovery address. Returns an error
    /// (no recovery address, or asked again too soon), or null.
    /// </summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SendResetCodeAsync(MailboxRow mailbox, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        if (doc.RecoveryAddress.Length == 0)
        {
            return "This account has no recovery address.";
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (doc.Reset is not null && now - doc.Reset.SentAt < TimeSpan.FromMinutes(1))
        {
            return "A code was sent less than a minute ago. Wait a moment, then ask for a new one.";
        }
        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        doc.Reset = new ResetState { CodeHash = ResetHash(mailbox.Id, code), Expires = now + ResetCodeLife, SentAt = now };
        await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
        string org = (await this.GetContextAsync(mailbox.Id, ct).ConfigureAwait(false)) is { } context && context.Tenant.DisplayName.Trim().Length > 0
            ? context.Tenant.DisplayName.Trim()
            : mailbox.Domain;
        string text = $"Your code to reset the password of {mailbox.Address} is:\r\n\r\n    {code}\r\n\r\n"
            + "It works for 10 minutes. If you did not ask for it, someone typed your user name on the sign-in page; your password has not changed, and you can ignore this message.\r\n\r\n"
            + $"{org} mail";
        return await this.SendSystemMailAsync(mailbox, doc.RecoveryAddress, $"{code} is your {org} mail reset code", text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Check a reset code (or a backup code) and set the new password. Returns
    /// the error, or null. Five wrong codes spend the code.
    /// </summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="code">The six-digit code, or a backup code.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="backup">True when <paramref name="code"/> is a backup code.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> ResetPasswordAsync(MailboxRow mailbox, string code, string newPassword, bool backup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(newPassword);
        SignInPolicy policy = await this.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        if (await Anjal.Smtp.PasswordPolicy.CheckAsync(newPassword, mailbox.Address, mailbox.DisplayName, ResetLength(policy), ct).ConfigureAwait(false) is string rejected)
        {
            return rejected;
        }
        if (backup && policy.Methods == "no-backup")
        {
            return "Your organisation does not accept backup codes.";
        }
        if (backup)
        {
            if (!await this.UseBackupCodeAsync(mailbox.Id, code, ct).ConfigureAwait(false))
            {
                return "That backup code is not right, or it has been used.";
            }
        }
        else
        {
            SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
            ResetState? reset = doc.Reset;
            if (reset is null || reset.Expires < DateTimeOffset.UtcNow || reset.Attempts >= 5)
            {
                return "That code has expired. Ask for a new one.";
            }
            string typed = new string(code.Where(char.IsAsciiDigit).ToArray());
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ResetHash(mailbox.Id, typed)), Encoding.ASCII.GetBytes(reset.CodeHash)))
            {
                reset.Attempts++;
                await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
                return reset.Attempts >= 5 ? "That code is not right, and it has now been tried too often. Ask for a new one." : "That code is not right. Check the message and try again.";
            }
        }
        return await this.SetPasswordAsync(mailbox, newPassword, ResetLength(policy), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invite someone to their mailbox (the organisation console sends these):
    /// a link that works for seven days, once. Returns the link's secret.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="invitedBy">Who invites, by name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The token for /invite/{token}, and when it expires.</returns>
    public async Task<(string Token, DateTimeOffset Expires)> CreateInvitationAsync(Guid mailboxId, string invitedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitedBy);
        string token = WebAuthn.ToBase64Url(mailboxId.ToByteArray().Concat(RandomNumberGenerator.GetBytes(24)).ToArray());
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        doc.Invitation = new InvitationState { Hash = Sha256Hex(token), Expires = now + InvitationLife, InvitedBy = invitedBy.Trim(), Created = now };
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        return (token, doc.Invitation.Expires);
    }

    /// <summary>The mailbox and invitation a link names, while it is still good; null otherwise.</summary>
    /// <param name="token">The link's token.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailbox and the invitation, or null.</returns>
    public async Task<(MailboxRow Mailbox, InvitationState Invitation, string Organisation)?> FindInvitationAsync(string? token, CancellationToken ct = default)
    {
        byte[]? raw = WebAuthn.FromBase64Url(token);
        if (raw is null || raw.Length != 40)
        {
            return null;
        }
        var mailboxId = new Guid(raw.AsSpan(0, 16));
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        InvitationState? invitation = doc.Invitation;
        if (invitation is null || invitation.Expires < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(invitation.Hash), Encoding.ASCII.GetBytes(Sha256Hex(token!))))
        {
            return null;
        }
        string org = context.Value.Tenant.DisplayName.Trim().Length > 0 ? context.Value.Tenant.DisplayName.Trim() : context.Value.Tenant.Slug;
        return (context.Value.Mailbox, invitation, org);
    }

    /// <summary>Accept an invitation: the first password and, if given, the recovery address. Returns the error, or null.</summary>
    /// <param name="token">The link's token.</param>
    /// <param name="password">The password chosen.</param>
    /// <param name="recovery">A recovery address, or empty.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AcceptInvitationAsync(string token, string password, string recovery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(recovery);
        (MailboxRow Mailbox, InvitationState Invitation, string Organisation)? found = await this.FindInvitationAsync(token, ct).ConfigureAwait(false);
        if (found is null)
        {
            return "This invitation has expired or has been used. Ask your administrator for a new one.";
        }
        MailboxRow mailbox = found.Value.Mailbox;
        string r = recovery.Trim();
        if (r.Length > 0 && RecoveryProblem(r, mailbox.Address) is string bad)
        {
            return bad;
        }
        if (await this.SetPasswordAsync(mailbox, password, ct).ConfigureAwait(false) is string error)
        {
            return error;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        doc.Invitation = null;
        if (r.Length > 0)
        {
            doc.RecoveryAddress = r.ToLowerInvariant();
        }
        await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The signed-in devices kept for a mailbox.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<List<SessionRecord>> GetSessionsAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<List<SessionRecord>>(mailboxId, SessionsKind, ct).ConfigureAwait(false) ?? new List<SessionRecord>();

    /// <summary>Save the signed-in devices.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="sessions">The list.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task SaveSessionsAsync(Guid mailboxId, List<SessionRecord> sessions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return this.WriteDocumentAsync(mailboxId, SessionsKind, sessions, ct);
    }

    /// <summary>
    /// Send a short message from the server itself (a reset code): from
    /// no-reply at the mailbox's domain, delivered here when the address is
    /// a mailbox on this server, queued otherwise. Returns the error, or null.
    /// </summary>
    /// <param name="mailbox">The mailbox it concerns.</param>
    /// <param name="to">The address.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="text">The text.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string?> SendSystemMailAsync(MailboxRow mailbox, string to, string subject, string text, CancellationToken ct = default) =>
        this.SystemMailAsync(mailbox, to, subject, text, false, ct);

    /// <summary>A security mail from the server (DES-11 S2-S5): as <see cref="SendSystemMailAsync"/>, marked so that it is delivered even to a full mailbox (DES-11 D2).</summary>
    /// <param name="mailbox">The mailbox it concerns.</param>
    /// <param name="to">The address.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="text">The text.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> SendSecurityMailAsync(MailboxRow mailbox, string to, string subject, string text, CancellationToken ct = default) =>
        this.SystemMailAsync(mailbox, to, subject, text, true, ct);

    private async Task<string?> SystemMailAsync(MailboxRow mailbox, string to, string subject, string text, bool security, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(text);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string from = "no-reply@" + mailbox.Domain;
        var sb = new StringBuilder();
        sb.Append("From: \"Anjal\" <").Append(from).Append(">\r\n");
        sb.Append("To: <").Append(to).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeaderText(subject)).Append("\r\n");
        sb.Append("Date: ").Append(FormatDate(now)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(mailbox.Domain).Append(">\r\n");
        sb.Append("Auto-Submitted: auto-generated\r\n");
        if (security)
        {
            sb.Append(SecurityMailHeader).Append(": 1\r\n");
        }
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n");
        byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        if (await sink.ResolveAsync(to, ct).ConfigureAwait(false) is not null)
        {
            Anjal.Smtp.DeliveryResult delivered = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
            {
                EnvelopeFrom = string.Empty,
                EnvelopeTo = new[] { to },
                RawBytes = raw,
                AnjalSecurityMail = security,
            }, ct).ConfigureAwait(false);
            return delivered.Outcome == Anjal.Smtp.DeliveryOutcome.Accepted ? null : "The code could not be delivered. Ask your administrator.";
        }
        await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = string.Empty,
            EnvelopeTo = to,
            RawBytes = raw,
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = now + TimeSpan.FromHours(1),
        }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>SHA-256 of text, lower-case hex.</summary>
    /// <param name="text">The text.</param>
    /// <returns>64 hex digits.</returns>
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    /// <summary>
    /// The shortest password for a person (owner, 10 Oct 2026): 15 characters when the password is
    /// used alone, 12 when they sign in with two-step or their organisation requires it of them - or
    /// the organisation's own length if longer.
    /// </summary>
    /// <param name="mailbox">The person.</param>
    /// <param name="doc">Their security document, when already read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The length in characters.</returns>
    public async Task<int> PasswordLengthForAsync(MailboxRow mailbox, SecurityDocument? doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        SignInPolicy policy = await this.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        doc ??= await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        bool twoStep = doc.TwoStepOn || await this.TwoStepRequiredAsync(policy, mailbox.Id, ct).ConfigureAwait(false);
        return policy.LengthFor(twoStep);
    }

    /// <summary>
    /// The shortest password on the reset page, the same for everyone in the organisation: 15, or 12
    /// when the organisation requires two-step of everyone (or its own length if longer). The page is
    /// reached without signing in, so it must not tell whether an account has two-step on.
    /// </summary>
    /// <param name="policy">The organisation's sign-in rules.</param>
    /// <returns>The length in characters.</returns>
    public static int ResetLength(SignInPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.LengthFor(policy.TwoStep == "everyone");
    }

    /// <summary>
    /// After a sign-in with the right password (owner, 10 Oct 2026): a password shorter than the rules
    /// now ask for this person is marked, so a new one is asked for before anything else; and the
    /// count of wrong passwords starts again.
    /// </summary>
    /// <param name="mailbox">The person.</param>
    /// <param name="password">The password as typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a longer password is now asked for.</returns>
    public async Task<bool> PasswordAcceptedAsync(MailboxRow mailbox, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(password);
        SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        bool below = Anjal.Smtp.PasswordPolicy.CharacterCount(password) < await this.PasswordLengthForAsync(mailbox, doc, ct).ConfigureAwait(false);
        if (below != doc.PasswordBelowRules || doc.WrongPasswords != 0 || doc.PasswordPausedUntil is not null)
        {
            doc.PasswordBelowRules = below;
            doc.WrongPasswords = 0;
            doc.PasswordPausedUntil = null;
            await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
        }
        return below;
    }

    /// <summary>
    /// A wrong password at sign-in for a person who exists (owner, 10 Oct 2026, P6): after
    /// <see cref="WrongPasswordLimit"/> in a row, signing in with the password pauses for
    /// <see cref="PasswordPause"/>. "Forgot password" still works.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Until when it is paused, when this wrong password paused it; otherwise null.</returns>
    public async Task<DateTimeOffset?> PasswordRefusedAsync(Guid personId, DateTimeOffset now, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        doc.WrongPasswords++;
        DateTimeOffset? paused = null;
        if (doc.WrongPasswords >= WrongPasswordLimit)
        {
            doc.WrongPasswords = 0;
            doc.PasswordPausedUntil = now + PasswordPause;
            paused = doc.PasswordPausedUntil;
        }
        await this.WriteDocumentAsync(personId, SecurityKind, doc, ct).ConfigureAwait(false);
        return paused;
    }

    /// <summary>True while signing in with the password is paused for a person (P6).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is paused.</returns>
    public async Task<bool> PasswordPausedAsync(Guid personId, DateTimeOffset now, CancellationToken ct = default) =>
        (await this.GetSecurityAsync(personId, ct).ConfigureAwait(false)).PasswordPausedUntil is DateTimeOffset until && until > now;

    private static bool UsedBefore(MailboxRow mailbox, SecurityDocument doc, string password, int depth)
    {
        if (mailbox.PasswordPbkdf2.Length > 0 && Anjal.Smtp.Pbkdf2Hasher.Verify(password, mailbox.PasswordPbkdf2))
        {
            return true;
        }
        foreach (string old in doc.PasswordHistory.Take(depth - 1))
        {
            if (Anjal.Smtp.Pbkdf2Hasher.Verify(password, old))
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> NewCodes(SecurityDocument doc)
    {
        var codes = new List<string>();
        for (int i = 0; i < BackupCodeCount; i++)
        {
            var sb = new StringBuilder();
            for (int j = 0; j < 8; j++)
            {
                if (j == 4)
                {
                    sb.Append('-');
                }
                sb.Append(BackupAlphabet[RandomNumberGenerator.GetInt32(BackupAlphabet.Length)]);
            }
            codes.Add(sb.ToString());
        }
        doc.BackupCodes = codes.Select(BackupHash).ToList();
        doc.BackupCodesMade = DateTimeOffset.UtcNow;
        return codes;
    }

    private static string BackupHash(string code) =>
        Sha256Hex("backup:" + new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()));

    private static string ResetHash(Guid mailboxId, string code) => Sha256Hex("reset:" + mailboxId.ToString("N") + ":" + code);

    private byte[]? Unprotect(string protectedSecret)
    {
        try
        {
            return Base32.Decode(this.Secrets.Unprotect(protectedSecret));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
