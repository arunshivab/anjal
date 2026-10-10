using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>Who administers an organisation (rc.14): their mailboxes.</summary>
public sealed class OrgRoles
{
    /// <summary>The administrators' mailboxes.</summary>
    public List<Guid> Admins { get; set; } = new();
}

/// <summary>
/// An organisation's sign-in rules (rc.14, board OrgSignin). Anjal sets the
/// outer limits; an organisation may only make them stricter.
/// </summary>
public sealed class SignInPolicy
{
    /// <summary>
    /// Anjal's shortest password: 12 with two-step sign-in, 15 without (owner, 10 Oct 2026). An
    /// organisation may ask for more, for everyone.
    /// </summary>
    public const int AnjalMinLength = Anjal.Smtp.PasswordPolicy.MinimumLength;

    /// <summary>The longest an organisation may ask for.</summary>
    public const int MaxMinLength = 64;

    /// <summary>Anjal's longest idle time on a shared computer, in minutes.</summary>
    public const int AnjalSharedIdleMinutes = 30;

    /// <summary>Anjal's longest idle time on a person's own device, in hours.</summary>
    public const int AnjalOwnIdleHours = 12;

    /// <summary>Anjal's longest sign-in, in days.</summary>
    public const int AnjalStayDays = 30;

    /// <summary>Anjal's longest trust of a device for two-step, in days.</summary>
    public const int AnjalTrustDays = 30;

    /// <summary>The choices for password length; 12 is Anjal's own rule (15 without two-step, 12 with).</summary>
    public static readonly IReadOnlyList<int> LengthChoices = new[] { 12, 15, 16, 20, 24 };

    /// <summary>The choices for refusing earlier passwords (owner, 10 Oct 2026, P8): none, the last 3, the last 5.</summary>
    public static readonly IReadOnlyList<int> HistoryChoices = new[] { 0, 3, 5 };

    /// <summary>The choices for idle on a shared computer, in minutes.</summary>
    public static readonly IReadOnlyList<int> SharedIdleChoices = new[] { 5, 10, 15, 20, 30 };

    /// <summary>The choices for idle on a person's own device, in hours.</summary>
    public static readonly IReadOnlyList<int> OwnIdleChoices = new[] { 1, 2, 4, 8, 12 };

    /// <summary>The choices for staying signed in, in days.</summary>
    public static readonly IReadOnlyList<int> StayChoices = new[] { 1, 7, 14, 30 };

    /// <summary>The choices for trusting a device, in days; 0 is never.</summary>
    public static readonly IReadOnlyList<int> TrustChoices = new[] { 0, 7, 14, 30 };

    /// <summary>The shortest password.</summary>
    public int MinLength { get; set; } = AnjalMinLength;

    /// <summary>
    /// Days before a password had to be changed; 0 never. No longer applied (owner, 10 Oct 2026, P3:
    /// NIST SP 800-63B-4 forbids forced periodic change); a stored value is kept as it is.
    /// </summary>
    public int ExpiryDays { get; set; }

    /// <summary>How many earlier passwords may not be used again: 0 (not checked, the default), 3 or 5 (owner, 10 Oct 2026, P8).</summary>
    public int PasswordHistory { get; set; }

    /// <summary>"optional", "admins" (required for administrators) or "everyone".</summary>
    public string TwoStep { get; set; } = "optional";

    /// <summary>"all" (authenticator, passkeys, backup codes, security keys) or "no-backup".</summary>
    public string Methods { get; set; } = "all";

    /// <summary>Idle minutes on a shared computer.</summary>
    public int SharedIdleMinutes { get; set; } = 15;

    /// <summary>Idle hours on a person's own device.</summary>
    public int OwnIdleHours { get; set; } = 8;

    /// <summary>Days a sign-in lasts on a person's own device.</summary>
    public int StayDays { get; set; } = AnjalStayDays;

    /// <summary>Days a device is trusted for two-step; 0 never.</summary>
    public int TrustDays { get; set; } = AnjalTrustDays;

    /// <summary>
    /// Whether people may keep their mail on their own devices to read
    /// offline (rc.15, item 65b, D-100): never on a shared computer, always
    /// encrypted, and each person's own choice where the organisation allows it.
    /// </summary>
    public bool OfflineMail { get; set; } = true;

    /// <summary>
    /// Whether the offline list shows subjects (DES-11 D5, owner 10 Oct 2026): an organisation may
    /// keep them off devices, so the list shows sender and date only.
    /// </summary>
    public bool OfflineSubjects { get; set; } = true;

    /// <summary>What is wrong with the rules, against Anjal's limits; null when they are within them.</summary>
    /// <returns>A sentence, or null.</returns>
    public string? Problem()
    {
        if (this.MinLength < AnjalMinLength || this.MinLength > MaxMinLength)
        {
            return $"Passwords must be at least {AnjalMinLength} characters, and an organisation may ask for up to {MaxMinLength}.";
        }
        if (!HistoryChoices.Contains(this.PasswordHistory))
        {
            return "Earlier passwords are not checked, or the last 3 or 5 are refused.";
        }
        if (this.TwoStep is not ("optional" or "admins" or "everyone") || this.Methods is not ("all" or "no-backup"))
        {
            return "Choose one of the two-step choices listed.";
        }
        if (this.SharedIdleMinutes < 1 || this.SharedIdleMinutes > AnjalSharedIdleMinutes)
        {
            return $"Idle sign-out on a shared computer is at most {AnjalSharedIdleMinutes} minutes.";
        }
        if (this.OwnIdleHours < 1 || this.OwnIdleHours > AnjalOwnIdleHours)
        {
            return $"Idle sign-out on a person's own device is at most {AnjalOwnIdleHours} hours.";
        }
        if (this.StayDays < 1 || this.StayDays > AnjalStayDays)
        {
            return $"Staying signed in is at most {AnjalStayDays} days.";
        }
        if (this.TrustDays < 0 || this.TrustDays > AnjalTrustDays)
        {
            return $"A device can be trusted for at most {AnjalTrustDays} days.";
        }
        return null;
    }

    /// <summary>The shortest password for a person: Anjal's rule for them, or the organisation's if longer.</summary>
    /// <param name="twoStep">True when the person signs in with a second step, or must.</param>
    /// <returns>The length in characters.</returns>
    public int LengthFor(bool twoStep) => Math.Max(this.MinLength, Anjal.Smtp.PasswordPolicy.LengthFor(twoStep));

    /// <summary>The idle limit for a kind of computer.</summary>
    /// <param name="shared">True for a shared computer.</param>
    /// <returns>The time.</returns>
    public TimeSpan Idle(bool shared) => shared ? TimeSpan.FromMinutes(this.SharedIdleMinutes) : TimeSpan.FromHours(this.OwnIdleHours);
}

/// <summary>An organisation's look on the sign-in page (rc.14, board OrgBranding).</summary>
public sealed class Branding
{
    /// <summary>The logo, as a data: address (PNG, JPEG, WebP or SVG); empty when none.</summary>
    public string Logo { get; set; } = string.Empty;

    /// <summary>The name shown to people, for example "Imagiqa mail"; empty uses the organisation's name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The theme a new person starts with.</summary>
    public string DefaultTheme { get; set; } = "anjal";

    /// <summary>The language a new person starts with.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>
    /// The clock everyone sees who has not chosen their own (owner, 8 Oct 2026): "24" (14:30) or "12"
    /// (2:30 pm). The 24-hour clock unless the organisation chooses otherwise; followed live.
    /// </summary>
    public string DefaultClock { get; set; } = Clocks.TwentyFour;

    /// <summary>One line under the sign-in form; empty for none.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>How long an organisation keeps mail and records (rc.14, board OrgRetention, policy CMP-02).</summary>
public sealed class RetentionPolicy
{
    /// <summary>Days in Trash for everyone who has not chosen their own (rc.15: followed live, including later changes).</summary>
    public int TrashDays { get; set; } = MailboxService.DefaultTrashDays;

    /// <summary>The longest a person may choose for Trash (rc.15, item 22): never over Anjal's 90, never under <see cref="TrashDays"/>.</summary>
    public int TrashMaxDays { get; set; } = MailboxService.MaxTrashDays;

    /// <summary>Days in Junk, for everyone.</summary>
    public int JunkDays { get; set; } = MailboxService.JunkDays;

    /// <summary>Days delivered Outbox copies are kept (Anjal removes them within 30).</summary>
    public int OutboxDays { get; set; } = 7;

    /// <summary>Days copies passed to applications are kept after they confirm (Anjal: within 30).</summary>
    public int AppCopyDays { get; set; } = 7;

    /// <summary>Years evidence is kept after its mail is deleted.</summary>
    public int EvidenceYears { get; set; } = 3;
}

/// <summary>A legal hold on one mailbox (rc.14): nothing is deleted from it until it is lifted.</summary>
/// <param name="MailboxId">The mailbox.</param>
/// <param name="Since">When it was placed.</param>
/// <param name="By">Who placed it.</param>
/// <param name="Reason">Why.</param>
public sealed record LegalHold(Guid MailboxId, DateTimeOffset Since, string By, string Reason);

/// <summary>A shared mailbox (rc.14, board OrgShared): no password of its own; people open it with the rights given.</summary>
public sealed class SharedMailbox
{
    /// <summary>"read", "send" (read and send) or "manage".</summary>
    public static readonly IReadOnlyList<string> Rights = new[] { "read", "send", "manage" };

    /// <summary>"as" (the mail is from the shared address) or "behalf" (the person on behalf of it).</summary>
    public string SendMode { get; set; } = "as";

    /// <summary>Keep a copy of sent mail in the shared mailbox's Sent.</summary>
    public bool KeepSentCopy { get; set; } = true;

    /// <summary>Each person's mailbox and right.</summary>
    public Dictionary<Guid, string> Members { get; set; } = new();
}

/// <summary>A key an application sends with (rc.14): an SMTP account that may send as one address.</summary>
public sealed class AppKey
{
    /// <summary>Its id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The SMTP user name it signs in with.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>A short fingerprint of the secret, for telling keys apart; the secret itself is not kept.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>When it stops working (an old key after a rotation); null while it is current.</summary>
    public DateTimeOffset? EndsAt { get; set; }
}

/// <summary>An application that sends mail (rc.14, board OrgApps).</summary>
public sealed class OrgApp
{
    /// <summary>Its id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Its name, for example "Hospital information system".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it does, in a few words.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The one address it may send as.</summary>
    public string SendsAs { get; set; } = string.Empty;

    /// <summary>What it receives, as a note (routing to a web address is set by the operator).</summary>
    public string Receives { get; set; } = string.Empty;

    /// <summary>Its keys.</summary>
    public List<AppKey> Keys { get; set; } = new();

    /// <summary>When it was added.</summary>
    public DateTimeOffset Created { get; set; }
}

/// <summary>What the operator records for an organisation (rc.14, board OpsOrgs).</summary>
public sealed class OpsRecord
{
    /// <summary>"active", "setting-up" or "trial"; suspended is the organisation being switched off.</summary>
    public string Status { get; set; } = "active";

    /// <summary>The most people.</summary>
    public int PeopleLimit { get; set; } = 50;

    /// <summary>The storage, in GB.</summary>
    public int StorageGb { get; set; } = 50;

    /// <summary>The most messages sent in a day.</summary>
    public int SendPerDay { get; set; } = 2000;

    /// <summary>When the data agreement was signed; null when not yet.</summary>
    public DateTimeOffset? AgreementSigned { get; set; }
}

/// <summary>One person in the organisation console (rc.14, board OrgPeople).</summary>
/// <param name="Mailbox">Their mailbox.</param>
/// <param name="Admin">True for an administrator.</param>
/// <param name="Status">"active", "must-change", "invited" or "disabled".</param>
/// <param name="TwoStep">How they sign in a second time, in words; empty when not set up.</param>
/// <param name="Devices">Signed-in devices.</param>
/// <param name="SharedDevices">Of which on a shared computer.</param>
/// <param name="Shared">The shared mailboxes they may open, in words.</param>
/// <param name="Held">True when under a legal hold.</param>
public sealed record PersonView(MailboxRow Mailbox, bool Admin, string Status, string TwoStep, int Devices, int SharedDevices, string Shared, bool Held);

/// <summary>One DNS record a domain needs, and what was found (rc.14, board OrgDomains).</summary>
/// <param name="Kind">MX, SPF, DKIM, DMARC, MTA-STS or TLS reports.</param>
/// <param name="Name">The name, relative to the domain ("@" is the domain itself).</param>
/// <param name="Type">MX or TXT.</param>
/// <param name="Value">The value to publish.</param>
/// <param name="Status">"pass", "missing", "different", "testing" or "unchecked".</param>
public sealed record DnsRecordCheck(string Kind, string Name, string Type, string Value, string Status);
