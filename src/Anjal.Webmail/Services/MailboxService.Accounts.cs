using System.Globalization;
using System.Security.Cryptography;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Who a person is (rc.22, owner's account rules of 15-18 Oct 2026), kept as their "profile"
/// document. The organisation keeps the first and last name; the name on mails is the mailbox's
/// display name, which an organisation may lock to first name + last name.
/// </summary>
public sealed class PersonProfile
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "profile";

    /// <summary>The version of the Terms the person accepted, or must accept.</summary>
    public const string CurrentTermsVersion = LegalText.Version;

    /// <summary>First name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Phone, recorded now and verified in a later release.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>True once the phone has been verified by code (a later release).</summary>
    public bool PhoneVerified { get; set; }

    /// <summary>Date of birth: required for individuals, optional for organisations; nobody under 18.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>The personal address the Terms and the invitation went to.</summary>
    public string PersonalEmail { get; set; } = string.Empty;

    /// <summary>When the Terms were accepted (at registration, or at the first sign-in); null until then.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }

    /// <summary>The version accepted.</summary>
    public string TermsVersion { get; set; } = string.Empty;

    /// <summary>True when the Terms must be accepted before anything opens (accounts made in rc.22 or later).</summary>
    public bool TermsRequired { get; set; }

    /// <summary>Who made the account: "registration", or the administrator's address.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>True for an individual (their own address on Anjal's shared domain); an individual id stays individual.</summary>
    public bool Individual { get; set; }

    /// <summary>First name and last name together.</summary>
    public string FullName => (this.FirstName.Trim() + " " + this.LastName.Trim()).Trim();
}

/// <summary>
/// An invitation waiting for its person (rc.22): nothing is created until they press Submit on
/// the registration page. Kept in the organisation's "pending" document.
/// </summary>
public sealed class PendingInvitation
{
    /// <summary>The tenant document kind.</summary>
    public const string Kind = "pending";

    /// <summary>Its id; the link's token starts with it.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 of the token, hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>True for an individual, who chooses their own username; false for an organisation's person.</summary>
    public bool Individual { get; set; }

    /// <summary>First name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>The username the administrator chose (organisations); empty for an individual.</summary>
    public string Local { get; set; } = string.Empty;

    /// <summary>The domain the address is on.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The personal address the invitation went to.</summary>
    public string Personal { get; set; } = string.Empty;

    /// <summary>Phone, when the administrator gave it.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Date of birth, when the administrator gave it.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>True to make them an administrator on registration.</summary>
    public bool Admin { get; set; }

    /// <summary>Who invited them, by name.</summary>
    public string InvitedBy { get; set; } = string.Empty;

    /// <summary>When.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Until when the link works (7 days).</summary>
    public DateTimeOffset Expires { get; set; }

    /// <summary>The address, when the username is known.</summary>
    public string Address => this.Local.Length > 0 ? this.Local + "@" + this.Domain : "@" + this.Domain;
}

/// <summary>A pending invitation found by its link.</summary>
/// <param name="Invitation">The invitation.</param>
/// <param name="Tenant">The organisation.</param>
/// <param name="Organisation">Its name.</param>
/// <param name="Individuals">True when the tenant is the individuals tenant.</param>
public sealed record PendingView(PendingInvitation Invitation, TenantRow Tenant, string Organisation, bool Individuals);

/// <summary>A person's request to have their account deleted (rc.22, item 40, DPDP), kept as their "deletion" document.</summary>
public sealed class DeletionRequest
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "deletion";

    /// <summary>When they asked.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>What they wrote, if anything.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>The accounts, sign-up and profile part of the service (rc.22).</summary>
public sealed partial class MailboxService
{
    /// <summary>The tenant document listing addresses that were deleted and are never given out again (rc.22, item 40).</summary>
    public const string RetiredKind = "retired";

    /// <summary>The shortest username.</summary>
    public const int MinUsername = 3;

    /// <summary>The longest username an individual may choose.</summary>
    public const int MaxUsername = 32;

    /// <summary>The youngest a person may be (owner, 15 Oct 2026: nobody under 18).</summary>
    public const int MinimumAge = 18;

    /// <summary>A person's profile; empty fields when they have none yet.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The profile.</returns>
    public async Task<PersonProfile> ProfileOfAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<PersonProfile>(mailboxId, PersonProfile.Kind, ct).ConfigureAwait(false) ?? new PersonProfile();

    /// <summary>Save a person's profile.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task SaveProfileAsync(Guid mailboxId, PersonProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.ForgetDemand(mailboxId);
        return this.WriteDocumentAsync(mailboxId, PersonProfile.Kind, profile, ct);
    }

    /// <summary>Whether a tenant is the individuals tenant (people on Anjal's shared domain, no organisation around them).</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True for individuals.</returns>
    public Task<bool> IsIndividualsTenantAsync(Guid tenantId, CancellationToken ct = default) => TenantKind.IsIndividualsAsync(this.store, tenantId, ct);

    /// <summary>Whether a mailbox belongs to an individual.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True for an individual.</returns>
    public async Task<bool> IsIndividualAsync(Guid mailboxId, CancellationToken ct = default)
    {
        Guid? tenant = await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false);
        return tenant is Guid t && await this.IsIndividualsTenantAsync(t, ct).ConfigureAwait(false);
    }

    /// <summary>An organisation's mail policy (forwarding outside, nicknames, away replies outside).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The policy.</returns>
    public Task<OrgMailPolicy> MailPolicyOfAsync(Guid tenantId, CancellationToken ct = default) => OrgMailPolicy.ReadAsync(this.store, tenantId, ct);

    /// <summary>Save an organisation's mail policy.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveMailPolicyAsync(Guid tenantId, OrgMailPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await this.store.SetTenantDocumentAsync(tenantId, OrgMailPolicy.Kind, MailDocuments.Write(policy), ct).ConfigureAwait(false);
        this.tenantDocs.TryRemove((tenantId, OrgMailPolicy.Kind), out _);
    }

    /// <summary>Whether a person may choose the name on their mails: individuals always; an organisation's people when it allows nicknames.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when they may.</returns>
    public async Task<bool> NicknamesAllowedAsync(Guid mailboxId, CancellationToken ct = default)
    {
        Guid? tenant = await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (tenant is not Guid t)
        {
            return false;
        }
        return await this.IsIndividualsTenantAsync(t, ct).ConfigureAwait(false) || (await this.MailPolicyOfAsync(t, ct).ConfigureAwait(false)).Nicknames;
    }

    /// <summary>
    /// The name on a person's mails (item 37): what they chose, when allowed; otherwise first name +
    /// last name. Returns the error, or null.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The name wanted.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetNameOnMailsAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            return "That mailbox was not found.";
        }
        string wanted = name.Trim();
        if (wanted.Length == 0)
        {
            return "Give a name: it goes on every mail you send.";
        }
        PersonProfile profile = await this.ProfileOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (!await this.NicknamesAllowedAsync(mailboxId, ct).ConfigureAwait(false) && !string.Equals(wanted, profile.FullName, StringComparison.Ordinal))
        {
            // DEF-130 (rc.23): also when the first and last name were never recorded - the administrator records them.
            return profile.FullName.Length == 0
                ? "Your organisation puts your first name and last name on your mails, and they have not been recorded yet. Ask your administrator to record them."
                : "Your organisation puts your first name and last name on your mails. Ask your administrator to change them.";
        }
        return await this.SetDisplayNameAsync(mailboxId, wanted, ct).ConfigureAwait(false);
    }

    /// <summary>What is wrong with a date of birth: missing when required, or under 18. Null when fine.</summary>
    /// <param name="dateOfBirth">The date, or null.</param>
    /// <param name="required">True for individuals.</param>
    /// <param name="today">Today.</param>
    /// <returns>The problem, or null.</returns>
    public static string? AgeProblem(DateOnly? dateOfBirth, bool required, DateOnly today)
    {
        if (dateOfBirth is not DateOnly dob)
        {
            return required ? "Give your date of birth. Anjal accounts are for people aged 18 or over." : null;
        }
        if (dob > today || dob.Year < today.Year - 120)
        {
            return "That date of birth cannot be right.";
        }
        DateOnly eighteenth = dob.AddYears(MinimumAge);
        return eighteenth > today ? "Anjal accounts are for people aged 18 or over." : null;
    }

    /// <summary>What is wrong with a username: length, letters, digits, dots; null when fine.</summary>
    /// <param name="local">The username typed.</param>
    /// <param name="individual">True for an individual (3 to 32 characters); an organisation may use up to 64.</param>
    /// <returns>The problem, or null.</returns>
    public static string? UsernameProblem(string? local, bool individual)
    {
        string u = (local ?? string.Empty).Trim().ToLowerInvariant();
        int max = individual ? MaxUsername : 64;
        if (u.Length < MinUsername || u.Length > max)
        {
            return individual ? "A username is 3 to 32 characters." : "A username is 3 to 64 characters.";
        }
        if (!char.IsAsciiLetterOrDigit(u[0]) || !char.IsAsciiLetterOrDigit(u[^1]) || u.Contains("..", StringComparison.Ordinal))
        {
            return "A username starts and ends with a letter or digit, with single dots between.";
        }
        foreach (char c in u)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
            {
                return "A username has letters, digits, dots, hyphens and underscores only.";
            }
        }
        if (u is "postmaster" or "abuse" or "unsubscribe" or "noreply" or "no-reply" or "anjal" or "usermanagement" or "welcome")
        {
            return "That username is reserved.";
        }
        return null;
    }

    /// <summary>What is wrong with a phone number; null when fine or empty.</summary>
    /// <param name="phone">The number typed.</param>
    /// <returns>The problem, or null.</returns>
    public static string? PhoneProblem(string? phone)
    {
        string p = (phone ?? string.Empty).Trim();
        if (p.Length == 0)
        {
            return null;
        }
        int digits = p.Count(char.IsAsciiDigit);
        if (digits < 7 || digits > 15 || p.Any(c => !char.IsAsciiDigit(c) && c != '+' && c != ' ' && c != '-' && c != '(' && c != ')'))
        {
            return "Give the phone with its country code, for example +91 98400 12345.";
        }
        return null;
    }

    /// <summary>
    /// Whether a username is free on a domain (checked live as the person leaves the box, item
    /// 5): not a mailbox, an extra address, a group, a waiting invitation or a retired address.
    /// With the person's names, a few free ones are suggested when it is taken.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="local">The username.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="firstName">For suggestions.</param>
    /// <param name="lastName">For suggestions.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is free, and suggestions when not.</returns>
    public async Task<(bool Free, IReadOnlyList<string> Suggestions)> UsernameFreeAsync(TenantRow tenant, string local, string domain, string firstName, string lastName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);
        string d = domain.Trim().ToLowerInvariant();
        bool free = await this.IsFreeAsync(tenant, local.Trim().ToLowerInvariant(), d, ct).ConfigureAwait(false);
        if (free)
        {
            return (true, Array.Empty<string>());
        }
        var suggestions = new List<string>();
        string first = Letters(firstName);
        string last = Letters(lastName);
        var candidates = new List<string>();
        if (first.Length > 0 && last.Length > 0)
        {
            candidates.Add(first + "." + last);
            candidates.Add(first + "." + last[0]);
            candidates.Add(last[0] + first);
            candidates.Add(first + last);
        }
        string stem = Letters(local);
        if (stem.Length == 0)
        {
            stem = first.Length > 0 ? first : "mail";
        }
        for (int n = 1; n <= 99 && candidates.Count < 8; n++)
        {
            candidates.Add(stem + RandomNumberGenerator.GetInt32(10, 1000).ToString(CultureInfo.InvariantCulture));
        }
        foreach (string c in candidates.Distinct(StringComparer.Ordinal))
        {
            if (suggestions.Count == 3)
            {
                break;
            }
            if (UsernameProblem(c, true) is null && await this.IsFreeAsync(tenant, c, d, ct).ConfigureAwait(false))
            {
                suggestions.Add(c);
            }
        }
        return (false, suggestions);
    }

    private async Task<bool> IsFreeAsync(TenantRow tenant, string local, string domain, CancellationToken ct)
    {
        if (local.Length == 0 || await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }
        string address = local + "@" + domain;
        AddressBook book = await AddressBook.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        if (book.AliasAt(address) is not null || book.GroupAt(address) is not null)
        {
            return false;
        }
        List<PendingInvitation> pending = await this.PendingOfAsync(tenant.Id, ct).ConfigureAwait(false);
        if (pending.Any(p => p.Local.Length > 0 && string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        List<string> retired = await this.ReadTenantDocumentAsync<List<string>>(tenant.Id, RetiredKind, ct).ConfigureAwait(false) ?? new List<string>();
        return !retired.Contains(address, StringComparer.OrdinalIgnoreCase);
    }

    private static string Letters(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>The invitations waiting in an organisation, those still in time.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The invitations, newest first.</returns>
    public async Task<List<PendingInvitation>> PendingOfAsync(Guid tenantId, CancellationToken ct = default)
    {
        List<PendingInvitation> all = await this.ReadTenantDocumentAsync<List<PendingInvitation>>(tenantId, PendingInvitation.Kind, ct).ConfigureAwait(false) ?? new List<PendingInvitation>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return all.Where(p => p.Expires > now).OrderByDescending(p => p.Created).ToList();
    }

    private async Task SavePendingAsync(Guid tenantId, List<PendingInvitation> pending, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await this.WriteTenantDocumentAsync(tenantId, PendingInvitation.Kind, pending.Where(p => p.Expires > now.AddDays(-7)).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invite a person (rc.22, item 5): nothing is created until they press Submit on the
    /// registration page. An organisation's administrator chooses the username; an individual
    /// chooses their own there. The link goes to the personal address and is returned too, to be
    /// shown once. Returns the error, or the token.
    /// </summary>
    /// <param name="tenant">The organisation (or the individuals tenant).</param>
    /// <param name="firstName">First name.</param>
    /// <param name="lastName">Last name.</param>
    /// <param name="local">The username (organisations); ignored for individuals.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="personal">The personal address the invitation goes to.</param>
    /// <param name="phone">Phone, or empty.</param>
    /// <param name="dateOfBirth">Date of birth, or null.</param>
    /// <param name="admin">True to make them an administrator.</param>
    /// <param name="invitedBy">Who invites, by name.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, string? Token)> InvitePendingAsync(TenantRow tenant, string firstName, string lastName, string local, string domain, string personal, string phone, DateOnly? dateOfBirth, bool admin, string invitedBy, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(invitedBy);
        ArgumentNullException.ThrowIfNull(baseUrl);
        bool individuals = await this.IsIndividualsTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        string first = Clip(firstName.Trim(), 60);
        string last = Clip(lastName.Trim(), 60);
        if (first.Length == 0 || last.Length == 0)
        {
            return ("Give the first name and the last name.", null);
        }
        string d = domain.Trim().ToLowerInvariant();
        IReadOnlyList<TenantDomainRow> domains = await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!domains.Any(x => string.Equals(x.Domain, d, StringComparison.OrdinalIgnoreCase)))
        {
            return ($"{d} is not one of this organisation's domains.", null);
        }
        string u = local.Trim().ToLowerInvariant();
        // The individuals tenant's own administrator (User management) is named by the operator, like an organisation's.
        bool choosesOwn = individuals && !admin;
        if (!choosesOwn)
        {
            // "usermanagement" is reserved for people, not for the operator naming that very account.
            if (UsernameProblem(u, individuals) is string bad && !(admin && u == "usermanagement"))
            {
                return (bad, null);
            }
            if (!await this.IsFreeAsync(tenant, u, d, ct).ConfigureAwait(false))
            {
                return ("That address is already taken.", null);
            }
        }
        string p = personal.Trim().ToLowerInvariant();
        if (p.Length == 0 && choosesOwn)
        {
            return ("Give the personal address the invitation goes to.", null);
        }
        if (p.Length > 0 && RecoveryProblem(p, u + "@" + d) is string badPersonal)
        {
            return (badPersonal, null);
        }
        if (PhoneProblem(phone) is string badPhone)
        {
            return (badPhone, null);
        }
        if (dateOfBirth is not null && AgeProblem(dateOfBirth, false, DateOnly.FromDateTime(DateTime.UtcNow)) is string badAge)
        {
            return (badAge, null);
        }
        if (await this.PeopleLimitReachedAsync(tenant, ct).ConfigureAwait(false) is string full)
        {
            return (full, null);
        }
        List<PendingInvitation> pending = await this.PendingOfAsync(tenant.Id, ct).ConfigureAwait(false);
        pending.RemoveAll(x => p.Length > 0 && string.Equals(x.Personal, p, StringComparison.OrdinalIgnoreCase) && (choosesOwn || string.Equals(x.Address, u + "@" + d, StringComparison.OrdinalIgnoreCase)));
        var invitation = new PendingInvitation
        {
            Individual = choosesOwn,
            FirstName = first,
            LastName = last,
            Local = choosesOwn ? string.Empty : u,
            Domain = d,
            Personal = p,
            Phone = Clip(phone.Trim(), 30),
            DateOfBirth = dateOfBirth,
            Admin = admin,
            InvitedBy = Clip(invitedBy.Trim(), 100),
            Created = DateTimeOffset.UtcNow,
            Expires = DateTimeOffset.UtcNow + InvitationLife,
        };
        string token = WebAuthn.ToBase64Url(invitation.Id.ToByteArray().Concat(RandomNumberGenerator.GetBytes(24)).ToArray());
        invitation.Hash = Sha256Hex(token);
        pending.Add(invitation);
        await this.SavePendingAsync(tenant.Id, pending, ct).ConfigureAwait(false);
        if (p.Length > 0)
        {
            await this.SendRegistrationMailAsync(tenant, invitation, token, baseUrl, ct).ConfigureAwait(false);
        }
        return (null, token);
    }

    private async Task<string?> PeopleLimitReachedAsync(TenantRow tenant, CancellationToken ct)
    {
        OpsRecord ops = await this.ReadTenantDocumentAsync<OpsRecord>(tenant.Id, OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
        int count = (await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false)).Count;
        return count >= ops.PeopleLimit ? $"This organisation has its {ops.PeopleLimit} people. Ask the Anjal operator to raise the limit." : null;
    }

    /// <summary>The invitation a registration link names, while it is still good; null otherwise.</summary>
    /// <param name="token">The link's token.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The invitation, or null.</returns>
    public async Task<PendingView?> FindPendingAsync(string? token, CancellationToken ct = default)
    {
        byte[]? raw = WebAuthn.FromBase64Url(token);
        if (raw is null || raw.Length != 40)
        {
            return null;
        }
        var id = new Guid(raw.AsSpan(0, 16));
        string hash = Sha256Hex(token!);
        foreach (TenantRow tenant in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            if (!tenant.Enabled)
            {
                continue;
            }
            List<PendingInvitation> pending = await this.PendingOfAsync(tenant.Id, ct).ConfigureAwait(false);
            PendingInvitation? found = pending.Find(p => p.Id == id);
            if (found is null)
            {
                continue;
            }
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(found.Hash), System.Text.Encoding.ASCII.GetBytes(hash)))
            {
                return null;
            }
            string org = tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : tenant.Slug;
            return new PendingView(found, tenant, org, await this.IsIndividualsTenantAsync(tenant.Id, ct).ConfigureAwait(false));
        }
        return null;
    }

    /// <summary>Withdraw an invitation that has not been used.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="id">The invitation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> WithdrawPendingAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        List<PendingInvitation> pending = await this.PendingOfAsync(tenantId, ct).ConfigureAwait(false);
        int removed = pending.RemoveAll(p => p.Id == id);
        if (removed > 0)
        {
            await this.SavePendingAsync(tenantId, pending, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>
    /// Submit on the registration page (item 5): the checks, then the mailbox is made, the Terms
    /// are recorded as accepted, the Terms email goes to the personal address and the Welcome mail
    /// is the first mail in the new Inbox. Returns the error, or the new mailbox.
    /// </summary>
    /// <param name="token">The link's token.</param>
    /// <param name="firstName">First name (individuals may correct it).</param>
    /// <param name="lastName">Last name.</param>
    /// <param name="phone">Phone.</param>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="local">The username (individuals choose; organisations' is fixed).</param>
    /// <param name="password">The password chosen.</param>
    /// <param name="termsAccepted">The Terms box was ticked.</param>
    /// <param name="disclaimerRead">The Disclaimer box was ticked.</param>
    /// <param name="baseUrl">The webmail's address.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, MailboxRow? Mailbox)> RegisterAsync(string? token, string firstName, string lastName, string phone, DateOnly? dateOfBirth, string local, string password, bool termsAccepted, bool disclaimerRead, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(baseUrl);
        PendingView? found = await this.FindPendingAsync(token, ct).ConfigureAwait(false);
        if (found is null)
        {
            return ("This invitation has expired or has been used. Ask for a new one.", null);
        }
        PendingInvitation inv = found.Invitation;
        bool individual = inv.Individual;
        string first = Clip((individual ? firstName : inv.FirstName).Trim(), 60);
        string last = Clip((individual ? lastName : inv.LastName).Trim(), 60);
        if (first.Length == 0 || last.Length == 0)
        {
            return ("Give your first name and your last name.", null);
        }
        string ph = Clip(phone.Trim(), 30);
        if (individual && ph.Length == 0)
        {
            return ("Give your phone number. It is recorded now and verified later.", null);
        }
        if (PhoneProblem(ph) is string badPhone)
        {
            return (badPhone, null);
        }
        DateOnly? dob = dateOfBirth ?? inv.DateOfBirth;
        if (AgeProblem(dob, individual, DateOnly.FromDateTime(DateTime.UtcNow)) is string badAge)
        {
            return (badAge, null);
        }
        string u = individual ? local.Trim().ToLowerInvariant() : inv.Local;
        if (individual && UsernameProblem(u, true) is string badName)
        {
            return (badName, null);
        }
        if (!await this.IsFreeAsync(found.Tenant, u, inv.Domain, ct).ConfigureAwait(false) && !(await this.PendingOfAsync(found.Tenant.Id, ct).ConfigureAwait(false)).Any(p => p.Id == inv.Id && string.Equals(p.Address, u + "@" + inv.Domain, StringComparison.OrdinalIgnoreCase)))
        {
            return ("That username is taken. Choose another.", null);
        }
        if (!termsAccepted || !disclaimerRead)
        {
            return ("Tick both boxes: the Terms of Service, and that you have read the Disclaimer.", null);
        }
        if (await this.PeopleLimitReachedAsync(found.Tenant, ct).ConfigureAwait(false) is string full)
        {
            return (full, null);
        }
        SignInPolicy policy = await this.SignInPolicyOfAsync(found.Tenant.Id, ct).ConfigureAwait(false);
        Anjal.Smtp.PasswordRuleResults rules = Anjal.Smtp.PasswordPolicy.Evaluate(password, u + "@" + inv.Domain, first + " " + last, policy.LengthFor(false));
        if (!rules.LongEnough || !rules.NotCommon || !rules.NotPersonal)
        {
            return ($"Choose a password of at least {policy.LengthFor(false)} characters that is not a common one and does not contain your name or address.", null);
        }
        var profile = new PersonProfile
        {
            FirstName = first,
            LastName = last,
            Phone = ph,
            DateOfBirth = dob,
            PersonalEmail = inv.Personal,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            TermsVersion = PersonProfile.CurrentTermsVersion,
            TermsRequired = true,
            CreatedBy = "registration",
            Individual = found.Individuals,
        };
        (string? error, MailboxRow? mailbox) = await this.CreateAccountAsync(found.Tenant, u, inv.Domain, profile, password, inv.Admin, false, ct).ConfigureAwait(false);
        if (error is not null || mailbox is null)
        {
            return (error ?? "The mailbox could not be made.", null);
        }
        await this.WithdrawPendingAsync(found.Tenant.Id, inv.Id, ct).ConfigureAwait(false);
        if (profile.PersonalEmail.Length > 0)
        {
            await this.SendTermsMailAsync(mailbox, profile, found.Organisation, inv.InvitedBy, baseUrl, ct).ConfigureAwait(false);
        }
        await this.SendWelcomeMailAsync(mailbox, profile, baseUrl, ct).ConfigureAwait(false);
        return (null, mailbox);
    }

    /// <summary>
    /// An administrator (or User management, for individuals) fills everything in (item 5): the
    /// mailbox is made at once with the password given, which the person changes at their first
    /// sign-in, where they also accept the Terms. The Terms email goes to the personal address and
    /// the Welcome mail into the new Inbox. Returns the error, or the mailbox.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="firstName">First name.</param>
    /// <param name="lastName">Last name.</param>
    /// <param name="local">The username.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="personal">The personal address.</param>
    /// <param name="phone">Phone.</param>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="password">The first password.</param>
    /// <param name="nameOnMails">The name on their mails; empty for first name + last name.</param>
    /// <param name="admin">True to make them an administrator.</param>
    /// <param name="by">The administrator's address.</param>
    /// <param name="baseUrl">The webmail's address.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, MailboxRow? Mailbox)> AddPersonAsync(TenantRow tenant, string firstName, string lastName, string local, string domain, string personal, string phone, DateOnly? dateOfBirth, string password, string nameOnMails, bool admin, string by, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(nameOnMails);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(baseUrl);
        bool individual = await this.IsIndividualsTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        string first = Clip(firstName.Trim(), 60);
        string last = Clip(lastName.Trim(), 60);
        if (first.Length == 0 || last.Length == 0)
        {
            return ("Give the first name and the last name.", null);
        }
        string d = domain.Trim().ToLowerInvariant();
        if (!(await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Any(x => string.Equals(x.Domain, d, StringComparison.OrdinalIgnoreCase)))
        {
            return ($"{d} is not one of this organisation's domains.", null);
        }
        string u = local.Trim().ToLowerInvariant();
        if (UsernameProblem(u, individual) is string badName)
        {
            return (badName, null);
        }
        if (!await this.IsFreeAsync(tenant, u, d, ct).ConfigureAwait(false))
        {
            return ("That address is already taken.", null);
        }
        string p = personal.Trim().ToLowerInvariant();
        if (p.Length == 0 || RecoveryProblem(p, u + "@" + d) is not null)
        {
            return ("Give the personal email the Terms are sent to.", null);
        }
        string ph = Clip(phone.Trim(), 30);
        if (individual && ph.Length == 0)
        {
            return ("Give the phone number. It is recorded now and verified later.", null);
        }
        if (PhoneProblem(ph) is string badPhone)
        {
            return (badPhone, null);
        }
        if (AgeProblem(dateOfBirth, individual, DateOnly.FromDateTime(DateTime.UtcNow)) is string badAge)
        {
            return (badAge, null);
        }
        if (await this.PeopleLimitReachedAsync(tenant, ct).ConfigureAwait(false) is string full)
        {
            return (full, null);
        }
        SignInPolicy policy = await this.SignInPolicyOfAsync(tenant.Id, ct).ConfigureAwait(false);
        Anjal.Smtp.PasswordRuleResults rules = Anjal.Smtp.PasswordPolicy.Evaluate(password, u + "@" + d, first + " " + last, policy.LengthFor(false));
        if (!rules.LongEnough || !rules.NotCommon || !rules.NotPersonal)
        {
            return ($"Choose a first password of at least {policy.LengthFor(false)} characters that is not a common one and does not contain their name or address.", null);
        }
        var profile = new PersonProfile
        {
            FirstName = first,
            LastName = last,
            Phone = ph,
            DateOfBirth = dateOfBirth,
            PersonalEmail = p,
            TermsRequired = true,
            CreatedBy = by,
            Individual = individual,
        };
        string name = Clip(nameOnMails.Trim(), 100);
        if (name.Length > 0 && !individual && !(await this.MailPolicyOfAsync(tenant.Id, ct).ConfigureAwait(false)).Nicknames)
        {
            name = string.Empty;
        }
        (string? error, MailboxRow? mailbox) = await this.CreateAccountAsync(tenant, u, d, profile, password, admin && !individual, true, ct, name).ConfigureAwait(false);
        if (error is not null || mailbox is null)
        {
            return (error ?? "The mailbox could not be made.", null);
        }
        string org = tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : tenant.Slug;
        await this.SendTermsMailAsync(mailbox, profile, org, by, baseUrl, ct).ConfigureAwait(false);
        await this.SendWelcomeMailAsync(mailbox, profile, baseUrl, ct).ConfigureAwait(false);
        return (null, mailbox);
    }

    private async Task<(string? Error, MailboxRow? Mailbox)> CreateAccountAsync(TenantRow tenant, string local, string domain, PersonProfile profile, string password, bool admin, bool mustChange, CancellationToken ct, string nameOnMails = "")
    {
        Branding branding = await this.ReadTenantDocumentAsync<Branding>(tenant.Id, BrandingKind, ct).ConfigureAwait(false) ?? new Branding();
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = local,
            Domain = domain,
            DisplayName = nameOnMails.Length > 0 ? nameOnMails : profile.FullName,
            Theme = MailboxRow.NormalizeTheme(branding.DefaultTheme + "-light"),
            Language = branding.DefaultLanguage,
            QuotaBytes = (await StoragePlan.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).OwnLimit(Guid.Empty),
        }, ct).ConfigureAwait(false);
        string? error = await this.SetPasswordAsync(mailbox, password, ct).ConfigureAwait(false);
        if (error is not null)
        {
            await this.store.DeleteMailboxAsync(local, domain, ct).ConfigureAwait(false);
            return (error, null);
        }
        await this.SaveProfileAsync(mailbox.Id, profile, ct).ConfigureAwait(false);
        SecurityDocument security = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
        security.RecoveryAddress = profile.PersonalEmail;
        security.MustChangePassword = mustChange;
        await this.WriteDocumentAsync(mailbox.Id, SecurityKind, security, ct).ConfigureAwait(false);
        if (admin)
        {
            await this.SetAdminAsync(tenant, mailbox.Id, true, ct).ConfigureAwait(false);
        }
        await this.store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox, ct).ConfigureAwait(false);
        return (null, await this.store.GetMailboxByIdAsync(mailbox.Id, ct).ConfigureAwait(false) ?? mailbox);
    }

    /// <summary>Accept the Terms at the first sign-in (item 5). Returns the error, or null.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="termsAccepted">The Terms box was ticked.</param>
    /// <param name="disclaimerRead">The Disclaimer box was ticked.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AcceptTermsAsync(Guid mailboxId, bool termsAccepted, bool disclaimerRead, CancellationToken ct = default)
    {
        if (!termsAccepted || !disclaimerRead)
        {
            return "Tick both boxes: the Terms of Service, and that you have read the Disclaimer.";
        }
        PersonProfile profile = await this.ProfileOfAsync(mailboxId, ct).ConfigureAwait(false);
        profile.TermsAcceptedAt = DateTimeOffset.UtcNow;
        profile.TermsVersion = PersonProfile.CurrentTermsVersion;
        await this.SaveProfileAsync(mailboxId, profile, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Whether a person must accept the Terms before anything opens.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the Terms are waiting.</returns>
    public async Task<bool> TermsWaitingAsync(Guid mailboxId, CancellationToken ct = default)
    {
        PersonProfile profile = await this.ProfileOfAsync(mailboxId, ct).ConfigureAwait(false);
        return profile.TermsRequired && profile.TermsAcceptedAt is null;
    }

    /// <summary>
    /// When an invitation's link stops working, written the same way in the mail and on the page
    /// (DEF-133, rc.23: the mail said the UTC date, the page the India date - a day apart near
    /// midnight): date and time on Anjal's clock, with the zone named.
    /// </summary>
    /// <param name="expires">When the link expires.</param>
    /// <param name="words">The reader's words, or null for English.</param>
    /// <returns>For example "26 Oct 2026, 14:05 India time".</returns>
    public static string InvitationExpiry(DateTimeOffset expires, Lexicon? words)
    {
        ZonedClock clock = ZonedClock.Default.In(words);
        string zone = words is null ? "India time" : words["India time"];
        return clock.Date(expires) + ", " + clock.Time(expires) + " " + zone;
    }

    private async Task SendRegistrationMailAsync(TenantRow tenant, PendingInvitation inv, string token, string baseUrl, CancellationToken ct)
    {
        string org = tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : tenant.Slug;
        string link = baseUrl.TrimEnd('/') + "/join/" + token;
        string text = inv.Individual
            ? $"{inv.InvitedBy} has invited you to your own Anjal mail address on {inv.Domain}.\r\n\r\n"
              + $"Fill in your details on the registration page - you choose your username and password there; the mailbox is made only when you press Submit:\r\n\r\n    {link}\r\n\r\n"
            : $"{inv.InvitedBy} has invited you to {org} mail. Your address will be {inv.Address}.\r\n\r\n"
              + $"Choose your password on the registration page; the mailbox is made only when you press Submit:\r\n\r\n    {link}\r\n\r\n";
        text += $"The link works once, until {InvitationExpiry(inv.Expires, null)}. Anjal accounts are for people aged 18 or over. If you were not expecting this, you can ignore this message.\r\n\r\n";
        // D-172 (rc.23, DEF-134): a real link to the help guide; the domain name above is not a website.
        text += $"How Anjal works, and answers to common questions:\r\n\r\n    {baseUrl.TrimEnd('/')}/help\r\n\r\n{org} mail";
        var carrier = new MailboxRow { TenantId = tenant.Id, LocalPart = "no-reply", Domain = inv.Domain, DisplayName = "Anjal" };
        await this.SystemMailFromAsync(carrier, org, inv.Personal, inv.Individual ? "Register your Anjal mail address" : $"Your {org} mail address is ready", text, ct).ConfigureAwait(false);
    }

    /// <summary>The Terms email (item 5): to the personal address, with the Terms and the Disclaimer linked.</summary>
    private async Task SendTermsMailAsync(MailboxRow mailbox, PersonProfile profile, string organisation, string by, string baseUrl, CancellationToken ct)
    {
        string origin = baseUrl.TrimEnd('/');
        string text = $"Dear {profile.FirstName},\r\n\r\n"
            + $"A mailbox {mailbox.Address} was created for you by {by} on {DateTimeOffset.UtcNow.UtcDateTime:d MMM yyyy}.\r\n\r\n"
            + "These are the Terms of Service and the Disclaimer that apply to it. "
            + (profile.TermsAcceptedAt is null ? "You will be asked to accept them when you first sign in.\r\n\r\n" : "You accepted them at registration; this copy is for your records.\r\n\r\n")
            + $"Terms of Service: {origin}/terms\r\nDisclaimer: {origin}/disclaimer\r\n\r\n"
            + $"Sign in at {origin}/sign-in" + (profile.TermsAcceptedAt is null ? " with the password you were given; you will be asked to choose your own." : ".") + "\r\n\r\n"
            + $"If you were not expecting this, write to {(await this.ContactAddressAsync(mailbox, ct).ConfigureAwait(false))}.\r\n\r\nAnjal";
        await this.SystemMailFromAsync(new MailboxRow { TenantId = mailbox.TenantId, LocalPart = "no-reply", Domain = mailbox.Domain, DisplayName = "Anjal" }, organisation, profile.PersonalEmail, "Your Anjal mailbox and the Terms of Service", text, ct).ConfigureAwait(false);
    }

    /// <summary>The Welcome mail (item 5): the first mail in a new Inbox.</summary>
    private async Task SendWelcomeMailAsync(MailboxRow mailbox, PersonProfile profile, string baseUrl, CancellationToken ct)
    {
        string origin = baseUrl.TrimEnd('/');
        string text = $"Your mail is ready: {mailbox.Address}.\r\n\r\n"
            + "Three things to do first:\r\n\r\n"
            + $"1. Turn on two-step sign-in in Settings > Security, so a password alone is never enough: {origin}/settings/security/two-step\r\n"
            + $"2. Choose your look - 21 colours, light or dark, and the size on screen - in Settings > Appearance: {origin}/settings/appearance\r\n"
            + $"3. Ask Lajna whenever you are stuck: press ? or the Lajna button at the bottom right. The guide is at {origin}/help\r\n\r\n"
            + "Mail you receive is kept exactly as it arrived, and nothing you write leaves this server unencrypted.\r\n\r\n"
            + $"Welcome,\r\nAnjal";
        await this.SystemMailFromAsync(new MailboxRow { TenantId = mailbox.TenantId, LocalPart = "welcome", Domain = mailbox.Domain, DisplayName = "Anjal" }, "Anjal", mailbox.Address, $"Welcome to Anjal, {profile.FirstName}", text, ct).ConfigureAwait(false);
    }

    private async Task<string> ContactAddressAsync(MailboxRow mailbox, CancellationToken ct)
    {
        TenantRow? tenant = await this.store.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        return tenant?.PostmasterMailbox is string p && p.Length > 0 ? p : "postmaster@" + mailbox.Domain;
    }

    /// <summary>Delete the people in the organisation who asked (item 40) - what the console shows as waiting.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailboxes with a deletion request, and when they asked.</returns>
    public async Task<Dictionary<Guid, DateTimeOffset>> DeletionRequestsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(tenantId, ct).ConfigureAwait(false))
        {
            if (await this.ReadDocumentAsync<DeletionRequest>(m.Id, DeletionRequest.Kind, ct).ConfigureAwait(false) is DeletionRequest r)
            {
                result[m.Id] = r.RequestedAt;
            }
        }
        return result;
    }

    /// <summary>A person's own deletion request, or null.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The request.</returns>
    public Task<DeletionRequest?> DeletionRequestOfAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.ReadDocumentAsync<DeletionRequest>(mailboxId, DeletionRequest.Kind, ct);

    /// <summary>
    /// Ask for the account to be deleted (item 40, DPDP): recorded, and the administrators (or
    /// User management) told by mail. The address is never given to anyone else afterwards.
    /// </summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="reason">What they wrote.</param>
    /// <param name="baseUrl">The webmail's address, for the link in the mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> RequestDeletionAsync(Guid mailboxId, string reason, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(baseUrl);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "That mailbox was not found.";
        }
        if (await this.IsHeldAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return "This mailbox is under a legal hold; it cannot be deleted until the hold is lifted.";
        }
        await this.WriteDocumentAsync(mailboxId, DeletionRequest.Kind, new DeletionRequest { RequestedAt = DateTimeOffset.UtcNow, Reason = Clip(reason, 500) }, ct).ConfigureAwait(false);
        string who = context.Value.Mailbox.Address;
        foreach (Guid admin in await this.AdminsOfAsync(context.Value.Tenant, ct).ConfigureAwait(false))
        {
            if (await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a && a.Id != mailboxId)
            {
                await this.SendSystemMailAsync(a, a.Address, $"{who} asks for their account to be deleted",
                    $"{who} asked on {DateTimeOffset.UtcNow.UtcDateTime:d MMM yyyy} for their account to be deleted, under the Digital Personal Data Protection Act.\r\n\r\n"
                    + (reason.Trim().Length > 0 ? "They wrote: " + Clip(reason, 500) + "\r\n\r\n" : string.Empty)
                    + $"Open People, choose them, and press Delete this account to confirm. The address is never given to anyone else; evidence copies are kept for the legal period and then erased.\r\n\r\n    {baseUrl.TrimEnd('/')}/org/people?sel={mailboxId}\r\n", ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    /// <summary>Withdraw a deletion request.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task WithdrawDeletionAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.SetMailboxDocumentAsync(mailboxId, DeletionRequest.Kind, string.Empty, ct);

    /// <summary>
    /// Delete an account (item 40): the administrator confirms; the mail files go, the rows go, the
    /// address is retired so it is never reissued. Evidence copies stay for their period. Returns
    /// the error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="mailboxId">The person.</param>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> DeleteAccountAsync(TenantRow tenant, Guid mailboxId, string maildirRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(maildirRoot);
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null || mailbox.TenantId != tenant.Id)
        {
            return "That person is not in this organisation.";
        }
        if (await this.IsHeldAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return "This mailbox is under a legal hold; lift it first.";
        }
        IReadOnlyList<Guid> admins = await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false);
        if (admins.Count == 1 && admins[0] == mailboxId)
        {
            return "This is the organisation's only administrator. Make someone else an administrator first.";
        }
        List<string> retired = await this.ReadTenantDocumentAsync<List<string>>(tenant.Id, RetiredKind, ct).ConfigureAwait(false) ?? new List<string>();
        if (!retired.Contains(mailbox.Address, StringComparer.OrdinalIgnoreCase))
        {
            retired.Add(mailbox.Address);
            await this.WriteTenantDocumentAsync(tenant.Id, RetiredKind, retired, ct).ConfigureAwait(false);
        }
        if (admins.Contains(mailboxId))
        {
            await this.SetAdminAsync(tenant, mailboxId, false, ct).ConfigureAwait(false);
        }
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        bool sharedChanged = false;
        foreach (SharedMailbox s in shared.Values)
        {
            sharedChanged |= s.Members.Remove(mailboxId);
        }
        if (sharedChanged)
        {
            await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, shared, ct).ConfigureAwait(false);
        }
        AddressBook book = await AddressBook.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        int aliasesRemoved = book.Aliases.RemoveAll(a => a.MailboxId == mailboxId);
        int membersRemoved = 0;
        foreach (GroupEntry g in book.Groups)
        {
            membersRemoved += g.Members.RemoveAll(m => m == mailboxId);
        }
        if (aliasesRemoved + membersRemoved > 0)
        {
            await AddressBook.WriteAsync(this.store, tenant.Id, book, ct).ConfigureAwait(false);
        }
        await this.store.DeleteMailboxAsync(mailbox.LocalPart, mailbox.Domain, ct).ConfigureAwait(false);
        this.tenantOfMailbox.TryRemove(mailboxId, out _);
        this.ForgetDemand(mailboxId);
        string folder = Path.Combine(maildirRoot, tenant.Slug, mailbox.Address);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            string exports = Path.Combine(maildirRoot, "exports", mailboxId.ToString("N"));
            if (Directory.Exists(exports))
            {
                Directory.Delete(exports, recursive: true);
            }
        }
        catch (IOException)
        {
            return "The rows are gone, but the mail files could not be removed; tell the Anjal operator.";
        }
        catch (UnauthorizedAccessException)
        {
            return "The rows are gone, but the mail files could not be removed; tell the Anjal operator.";
        }
        return null;
    }

    /// <summary>
    /// Delete a shared mailbox (DEF-135, DEF-163, rc.23): its people lose it, its address is never
    /// given out again, and its mail is erased - the evidence copies stay for their retention, as
    /// for a person's account. Refused under a legal hold. Returns the reason it was refused, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="maildirRoot">Where the mail files are kept.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reason, or null when it was deleted.</returns>
    public async Task<string?> DeleteSharedMailboxAsync(TenantRow tenant, Guid sharedId, string maildirRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(maildirRoot);
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!shared.ContainsKey(sharedId))
        {
            return "That is not one of this organisation's shared mailboxes.";
        }
        string? error = await this.DeleteAccountAsync(tenant, sharedId, maildirRoot, ct).ConfigureAwait(false);
        if (error is not null && await this.store.GetMailboxByIdAsync(sharedId, ct).ConfigureAwait(false) is not null)
        {
            return error;
        }
        shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (shared.Remove(sharedId))
        {
            await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, shared, ct).ConfigureAwait(false);
        }
        return error;
    }

    /// <summary>The other accounts signed in on a browser (rc.22, item 33), with their unread mail, for the initials menu.</summary>
    /// <param name="mailboxIds">Their mailboxes, from the browser's sealed cookie.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailboxes that still exist and are enabled, in the order given.</returns>
    public async Task<IReadOnlyList<(MailboxRow Mailbox, long Unread)>> OtherAccountsAsync(IReadOnlyList<Guid> mailboxIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxIds);
        var result = new List<(MailboxRow, long)>();
        foreach (Guid id in mailboxIds.Take(10))
        {
            MailboxRow? m = await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false);
            if (m is null || !m.Enabled)
            {
                continue;
            }
            FolderRow? inbox = await this.GetFolderAsync(id, FolderRow.Inbox, ct).ConfigureAwait(false);
            long unread = inbox is null ? 0 : await this.store.CountUnreadAsync(id, inbox.Id, ct).ConfigureAwait(false);
            result.Add((m, unread));
        }
        return result;
    }

    /// <summary>A person's away reply (rc.22, item 30).</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reply; off when none is set.</returns>
    public Task<AwayReply> AwayOfAsync(Guid mailboxId, CancellationToken ct = default) => AwayReply.ReadAsync(this.store, mailboxId, ct);

    /// <summary>Save a person's away reply, keeping who was already answered. Returns the error, or null.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="away">The reply.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveAwayAsync(Guid mailboxId, AwayReply away, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(away);
        if (away.On && away.Text.Trim().Length == 0)
        {
            return "Write the reply people will get.";
        }
        if (away.From is DateTimeOffset f && away.Until is DateTimeOffset u && u <= f)
        {
            return "Until must be after From.";
        }
        if (!AwayReply.ReplyToChoices.Contains(away.ReplyTo))
        {
            away.ReplyTo = "everyone";
        }
        AwayReply current = await this.AwayOfAsync(mailboxId, ct).ConfigureAwait(false);
        // Switched on afresh: everyone is answered once again.
        away.Replied = away.On && (!current.On || current.From != away.From) ? new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) : current.Replied;
        away.Subject = Clip(away.Subject, 200);
        away.Text = Clip(away.Text, 4000);
        away.Skip = Clip(away.Skip, 300);
        await AwayReply.WriteAsync(this.store, mailboxId, away, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Whether a person's away reply is on now (for the yellow line in the Inbox).</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reply when active, else null.</returns>
    public async Task<AwayReply?> AwayActiveAsync(Guid mailboxId, CancellationToken ct = default)
    {
        AwayReply away = await this.AwayOfAsync(mailboxId, ct).ConfigureAwait(false);
        return away.IsActive(DateTimeOffset.UtcNow) ? away : null;
    }

    /// <summary>Turn away replies off where their Until has passed (hourly): the setting then reads "off".</summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were turned off.</returns>
    public async Task<int> EndAwayRepliesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        int ended = 0;
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(null, ct).ConfigureAwait(false))
        {
            AwayReply away = await this.AwayOfAsync(m.Id, ct).ConfigureAwait(false);
            if (away.On && away.Until is DateTimeOffset until && until <= now)
            {
                away.On = false;
                await AwayReply.WriteAsync(this.store, m.Id, away, ct).ConfigureAwait(false);
                ended++;
            }
        }
        return ended;
    }

    /// <summary>How many messages wait in quarantine for a mailbox (rc.22, item 39).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    public async Task<int> HeldCountAsync(Guid mailboxId, CancellationToken ct = default)
    {
        FolderRow? held = await this.GetFolderAsync(mailboxId, AttachmentPolicy.QuarantineFolder, ct).ConfigureAwait(false);
        return held is null ? 0 : (int)await this.store.CountMessagesAsync(mailboxId, held.Id, ct).ConfigureAwait(false);
    }

    /// <summary>The addresses retired in an organisation (deleted accounts): never given out again.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The addresses.</returns>
    public async Task<IReadOnlyList<string>> RetiredAddressesAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<List<string>>(tenantId, RetiredKind, ct).ConfigureAwait(false) ?? new List<string>();

    /// <summary>
    /// A mail from the server for an organisation, with its own From address (no-reply@ or
    /// welcome@ the domain): delivered here when the address is a mailbox on this server, queued
    /// otherwise. Returns the error, or null.
    /// </summary>
    private async Task<string?> SystemMailFromAsync(MailboxRow from, string organisation, string to, string subject, string text, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var sb = new System.Text.StringBuilder();
        sb.Append("From: \"Anjal\" <").Append(from.Address).Append(">\r\n");
        sb.Append("To: <").Append(to).Append(">\r\n");
        sb.Append("Subject: ").Append(Anjal.Mailbox.MailboxSink.EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(from.Domain).Append(">\r\n");
        sb.Append("Auto-Submitted: auto-generated\r\n");
        string boundary = "anjal-" + Guid.NewGuid().ToString("N");
        string Crlf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        sb.Append("MIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(Crlf(text)).Append("\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(Crlf(SystemMailLayout.Html(SystemMailLayout.Notice, subject, text, organisation, from.Domain)));
        sb.Append("--").Append(boundary).Append("--\r\n");
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        if (await sink.ResolveAsync(to, ct).ConfigureAwait(false) is not null)
        {
            Anjal.Smtp.DeliveryResult delivered = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
            {
                EnvelopeFrom = string.Empty,
                EnvelopeTo = new[] { to },
                RawBytes = raw,
                // DEF-118 (rc.23): Anjal's own mail never crossed a network, so it is recorded as
                // mail from this server - not as "arrived without encryption".
                AuthenticatedUser = "anjal",
                AnjalSecurityMail = true,
            }, ct).ConfigureAwait(false);
            return delivered.Outcome == Anjal.Smtp.DeliveryOutcome.Accepted ? null : "The mail could not be delivered.";
        }
        await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = string.Empty,
            EnvelopeTo = to,
            RawBytes = raw,
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = now + OutboundMessage.DefaultGiveUp,
        }, ct).ConfigureAwait(false);
        return null;
    }
}
