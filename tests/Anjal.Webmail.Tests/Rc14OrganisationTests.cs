using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.14: the organisation console's service, piece by piece - sign-in
/// rules within Anjal's limits, administrators, invitations, shared
/// mailboxes and their rights, legal holds, retention, applications' keys,
/// domains, organisation templates, and the Anjal console's organisations
/// and health.
/// </summary>
public sealed class Rc14OrganisationTests : IDisposable
{
    private static readonly string[] RecordKinds = { "MX", "SPF", "DKIM", "DMARC", "MTA-STS", "TLS reports" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc14-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private readonly TenantRow tenant;
    private readonly TenantRow other;
    private readonly MailboxRow arun;
    private readonly MailboxRow meera;
    private readonly MailboxRow stranger;

    public Rc14OrganisationTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "mail.anjal.co.in");
        this.tenant = this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" }).GetAwaiter().GetResult();
        this.other = this.store.UpsertTenantAsync(new TenantRow { Slug = "other", DisplayName = "Other" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.other.Id, Domain = "other.example" }).GetAwaiter().GetResult();
        this.arun = this.Person(this.tenant, "arun", "anjal.co.in", "Arun Shiva B");
        this.meera = this.Person(this.tenant, "meera.iyer", "anjal.co.in", "Meera Iyer");
        this.stranger = this.Person(this.other, "someone", "other.example", "Someone");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void SignInPolicy_OnlyStricterThanAnjal()
    {
        Assert.Null(new SignInPolicy().Problem());
        Assert.Null(new SignInPolicy { MinLength = 12, ExpiryDays = 90, TwoStep = "everyone", Methods = "no-backup", SharedIdleMinutes = 5, OwnIdleHours = 1, StayDays = 1, TrustDays = 0 }.Problem());
        Assert.NotNull(new SignInPolicy { MinLength = 7 }.Problem());
        // Owner, 10 Oct 2026 (P3): expiry is no longer a rule; earlier passwords are none, 3 or 5 (P8).
        Assert.Null(new SignInPolicy { ExpiryDays = 10 }.Problem());
        Assert.NotNull(new SignInPolicy { PasswordHistory = 4 }.Problem());
        Assert.Null(new SignInPolicy { PasswordHistory = 3 }.Problem());
        Assert.NotNull(new SignInPolicy { SharedIdleMinutes = 31 }.Problem());
        Assert.NotNull(new SignInPolicy { OwnIdleHours = 13 }.Problem());
        Assert.NotNull(new SignInPolicy { StayDays = 31 }.Problem());
        Assert.NotNull(new SignInPolicy { TrustDays = 31 }.Problem());
        Assert.NotNull(new SignInPolicy { TwoStep = "sometimes" }.Problem());
        Assert.Equal(TimeSpan.FromMinutes(15), new SignInPolicy().Idle(shared: true));
        Assert.Equal(TimeSpan.FromHours(8), new SignInPolicy().Idle(shared: false));
    }

    [Fact]
    public async Task Policy_IsSavedPerOrganisation_AndAsksForTwoStepWhereRequired()
    {
        Assert.NotNull(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { MinLength = 4 }));
        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { MinLength = 12, TwoStep = "admins" }));
        Assert.Equal(12, (await this.svc.SignInPolicyForAsync(this.meera.Id)).MinLength);
        Assert.Equal(SignInPolicy.AnjalMinLength, (await this.svc.SignInPolicyForAsync(this.stranger.Id)).MinLength);
        // Arun is the administrator (the postmaster, before anyone is named): two-step is asked of him only.
        Assert.Equal("twostep", await this.svc.SignInDemandAsync(this.arun.Id));
        Assert.Null(await this.svc.SignInDemandAsync(this.meera.Id));
        // The organisation's length is applied when a password is set.
        Assert.NotNull(await this.svc.ChangePasswordAsync(this.meera.Id, "correct horse battery", "Shortpass-1", "Shortpass-1"));
    }

    [Fact]
    public async Task Administrators_ThePostmasterFirst_AndNeverNone()
    {
        Assert.True(await this.svc.IsOrgAdminAsync(this.arun.Id));
        Assert.False(await this.svc.IsOrgAdminAsync(this.meera.Id));
        Assert.Null(await this.svc.SetAdminAsync(this.tenant, this.meera.Id, true));
        Assert.True(await this.svc.IsOrgAdminAsync(this.meera.Id));
        Assert.Null(await this.svc.SetAdminAsync(this.tenant, this.arun.Id, false));
        Assert.NotNull(await this.svc.SetAdminAsync(this.tenant, this.meera.Id, false));
        Assert.True(await this.svc.IsOrgAdminAsync(this.meera.Id));
    }

    [Fact]
    public async Task Invite_OnlyAtTheOrganisationsDomains_WithinItsPeopleLimit()
    {
        Assert.NotNull((await this.svc.InviteAsync(this.tenant, "X", "x@other.example", string.Empty, false, "Arun", "https://mail.anjal.co.in")).Error);
        Assert.NotNull((await this.svc.InviteAsync(this.tenant, "X", "meera.iyer@anjal.co.in", string.Empty, false, "Arun", "https://mail.anjal.co.in")).Error);
        (string? error, string? token) = await this.svc.InviteAsync(this.tenant, "Rahul Desai", "Rahul.Desai@anjal.co.in", string.Empty, false, "Arun", "https://mail.anjal.co.in");
        Assert.Null(error);
        Assert.Equal("rahul.desai@anjal.co.in", (await this.svc.FindInvitationAsync(token))!.Value.Mailbox.Address);
        using var sessions = new SessionRegistry(this.svc);
        PersonView rahul = (await this.svc.ListPeopleAsync(this.tenant, sessions)).Single(p => p.Mailbox.LocalPart == "rahul.desai");
        Assert.Equal("invited", rahul.Status);
        Assert.False(rahul.Admin);

        await this.svc.WriteTenantDocumentAsync(this.tenant.Id, MailboxService.OpsKind, new OpsRecord { PeopleLimit = 3 });
        Assert.Contains("3 people", (await this.svc.InviteAsync(this.tenant, "Y", "y@anjal.co.in", string.Empty, false, "Arun", "https://mail.anjal.co.in")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedMailbox_RightsAreGivenToItsOwnPeopleOnly_AndOnBehalfNamesTheWriter()
    {
        (string? error, Guid? id) = await this.svc.CreateSharedMailboxAsync(this.tenant, "operations@anjal.co.in", "Operations");
        Assert.Null(error);
        Assert.NotNull((await this.svc.CreateSharedMailboxAsync(this.tenant, "operations@other.example", "Ops")).Error);
        Guid ops = id!.Value;
        Assert.Null(await this.svc.SetSharedRightAsync(this.tenant, ops, this.meera.Id, "send"));
        Assert.NotNull(await this.svc.SetSharedRightAsync(this.tenant, ops, this.stranger.Id, "read"));
        Assert.Equal("send", await this.svc.SharedRightAsync(this.meera.Id, ops));
        Assert.Null(await this.svc.SharedRightAsync(this.arun.Id, ops));
        Assert.Null(await this.svc.SharedRightAsync(this.stranger.Id, ops));
        Assert.Single(await this.svc.SharedForPersonAsync(this.meera.Id));
        // A shared mailbox is not a person.
        using var sessions = new SessionRegistry(this.svc);
        Assert.DoesNotContain(await this.svc.ListPeopleAsync(this.tenant, sessions), p => p.Mailbox.Id == ops);

        Assert.Equal(string.Empty, await this.svc.SenderForAsync(this.meera.Id, ops));
        Assert.True(await this.svc.SetSharedSendingAsync(this.tenant, ops, "behalf", keepSentCopy: false));
        Assert.Contains("meera.iyer@anjal.co.in", await this.svc.SenderForAsync(this.meera.Id, ops), StringComparison.Ordinal);

        Assert.Null(await this.svc.SetSharedRightAsync(this.tenant, ops, this.meera.Id, null));
        Assert.Null(await this.svc.SharedRightAsync(this.meera.Id, ops));
    }

    [Fact]
    public async Task LegalHold_NeedsAReason_AndStopsEveryKindOfDeletion()
    {
        Assert.NotNull(await this.svc.SetHoldAsync(this.tenant, this.meera.Id, true, "Arun", " "));
        Assert.NotNull(await this.svc.SetHoldAsync(this.tenant, this.stranger.Id, true, "Arun", "inquiry"));
        Assert.Null(await this.svc.SetHoldAsync(this.tenant, this.meera.Id, true, "Arun", "internal inquiry"));
        Assert.True(await this.svc.IsHeldAsync(this.meera.Id));
        Assert.Equal("internal inquiry", Assert.Single(await this.svc.HoldsOfAsync(this.tenant.Id)).Reason);
        Assert.Equal(0, await this.svc.EmptyTrashAsync(this.meera.Id));
        Assert.Equal(0, await this.svc.EmptyOldAsync(this.meera.Id, DateTimeOffset.UtcNow.AddYears(1)));
        Assert.Null(await this.svc.SetHoldAsync(this.tenant, this.meera.Id, false, "Arun", string.Empty));
        Assert.False(await this.svc.IsHeldAsync(this.meera.Id));
    }

    [Fact]
    public async Task Retention_WithinAnjalsLimits_AndEvidenceYearsReachTheOrganisation()
    {
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 3 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { JunkDays = 120 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { OutboxDays = 31 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { EvidenceYears = 11 }));
        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 14, JunkDays = 30, OutboxDays = 1, AppCopyDays = 14, EvidenceYears = 5 }));
        Assert.Equal(5 * 365, (await this.store.GetTenantByIdAsync(this.tenant.Id))!.EvidenceRetentionDays);
        RetentionPolicy saved = await this.svc.RetentionOfAsync(this.tenant);
        Assert.Equal(14, saved.TrashDays);
        Assert.Equal(5, saved.EvidenceYears);
    }

    [Fact]
    public async Task AppKeys_SendAsOneAddress_RotateWithOverlap_AndRevokeAtOnce()
    {
        Assert.NotNull((await this.svc.AddAppAsync(this.tenant, "HIS", string.Empty, "notifications@other.example", string.Empty)).Error);
        (string? error, string? appId, string? user, string? secret) = await this.svc.AddAppAsync(this.tenant, "Hospital information system", "Sends notifications", "notifications@anjal.co.in", string.Empty);
        Assert.Null(error);
        SmtpUserRow row = (await this.store.GetSmtpUserAsync(user!))!;
        Assert.Equal("notifications@anjal.co.in", Assert.Single(row.AllowedFromDomains));
        Assert.True(Pbkdf2Hasher.Verify(secret!, row.PasswordPbkdf2));
        // Anjal keeps a fingerprint, never the secret.
        OrgApp app = Assert.Single(await this.svc.AppsOfAsync(this.tenant.Id));
        Assert.DoesNotContain(secret!, System.Text.Json.JsonSerializer.Serialize(app), StringComparison.Ordinal);

        (string Username, string Secret)? second = await this.svc.NewKeyAsync(this.tenant, appId!, rotate: true);
        Assert.NotNull(second);
        app = Assert.Single(await this.svc.AppsOfAsync(this.tenant.Id));
        Assert.Equal(2, app.Keys.Count);
        AppKey old = app.Keys.Single(k => k.Username == user);
        Assert.NotNull(old.EndsAt);
        Assert.Equal(0, await this.svc.RetireEndedKeysAsync(DateTimeOffset.UtcNow.AddDays(13)));
        Assert.Equal(1, await this.svc.RetireEndedKeysAsync(DateTimeOffset.UtcNow.AddDays(15)));
        Assert.Null(await this.store.GetSmtpUserAsync(user!));

        AppKey live = Assert.Single(Assert.Single(await this.svc.AppsOfAsync(this.tenant.Id)).Keys);
        Assert.True(await this.svc.RevokeKeyAsync(this.tenant, appId!, live.Id));
        Assert.Null(await this.store.GetSmtpUserAsync(second!.Value.Username));
    }

    [Fact]
    public async Task Domains_AProvedDomainGetsADkimKey_AndOneOrganisationOwnsIt()
    {
        Assert.Equal("That domain is already in use on this service.", await this.svc.AddTenantDomainAsync(this.tenant, "other.example"));
        Assert.Null(await this.svc.AddTenantDomainAsync(this.tenant, "Clinic.Example"));
        Assert.Equal(this.tenant.Id, (await this.svc.TenantDomainAsync("clinic.example"))!.TenantId);
        Assert.Equal(MailboxService.DkimSelector, (await this.store.GetDkimKeyAsync("clinic.example"))!.Selector);
        IReadOnlyList<DnsRecordCheck> records = await this.svc.DomainRecordsAsync("clinic.example", null);
        Assert.Equal(RecordKinds, records.Select(r => r.Kind).ToArray());
        Assert.StartsWith("v=DKIM1; k=rsa; p=", records.Single(r => r.Kind == "DKIM").Value, StringComparison.Ordinal);
        Assert.Equal("anjal._domainkey", records.Single(r => r.Kind == "DKIM").Name);
        Assert.All(records, r => Assert.Equal("unchecked", r.Status));
    }

    [Fact]
    public async Task OrganisationTemplates_AreOfferedToEveryoneInIt()
    {
        Assert.Null(await this.svc.SaveOrgTemplateAsync(this.tenant.Id, "Leave approved", "Leave approved", "Dear {Name}, your leave is approved."));
        MailTemplate t = Assert.Single(await this.svc.OrgTemplatesForAsync(this.meera.Id));
        Assert.Equal(MailboxService.OrgTemplatesGroup, t.Group);
        Assert.Equal("Leave approved", (await this.svc.FindTemplateAsync(this.meera.Id, t.Key))!.Name);
        Assert.Empty(await this.svc.OrgTemplatesForAsync(this.stranger.Id));
        Assert.Null(await this.svc.FindTemplateAsync(this.stranger.Id, t.Key));
        Assert.True(await this.svc.DeleteOrgTemplateAsync(this.tenant.Id, t.Key));
    }

    [Fact]
    public async Task TrustedDevice_CountsOnlyWithinTheOrganisationsTrust()
    {
        string token = await this.svc.TrustDeviceAsync(this.meera.Id, "Laptop");
        Assert.True(await this.svc.IsTrustedDeviceAsync(this.meera.Id, token));
        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { TrustDays = 0 }));
        Assert.False(await this.svc.IsTrustedDeviceAsync(this.meera.Id, token));
    }

    [Fact]
    public async Task Operator_AddsAnOrganisation_ThenSuspendsAndResumesIt()
    {
        (string? error, TenantRow? clinic, string? token) = await this.svc.CreateOrganisationAsync("Example Clinic", "ExampleClinic.in", "Clinic Admin", "admin@exampleclinic.in", string.Empty, "setting-up", "Operator", "https://mail.anjal.co.in");
        Assert.Null(error);
        Assert.NotNull(token);
        Assert.Equal("exampleclinic", clinic!.Slug);
        Assert.NotNull((await this.svc.CreateOrganisationAsync("Again", "exampleclinic.in", "A", "a@exampleclinic.in", string.Empty, "trial", "Operator", "https://x")).Error);
        Assert.NotNull((await this.svc.CreateOrganisationAsync("Wrong", "wrong.example", "A", "a@elsewhere.example", string.Empty, "trial", "Operator", "https://x")).Error);

        OrgSummary summary = (await this.svc.ListOrganisationsAsync()).Single(o => o.Tenant.Id == clinic.Id);
        Assert.Equal("setting-up", summary.Status);
        Assert.Equal(1, summary.People);
        Assert.Equal("admin@exampleclinic.in", Assert.Single(summary.Admins));
        Assert.Empty(summary.DkimMissing);

        Assert.NotNull(await this.svc.SaveOpsRecordAsync(clinic.Id, new OpsRecord { PeopleLimit = 0 }));
        Assert.Null(await this.svc.SaveOpsRecordAsync(clinic.Id, new OpsRecord { Status = "active", PeopleLimit = 40 }));

        Guid admin = (await this.svc.FindInvitationAsync(token))!.Value.Mailbox.Id;
        Assert.NotNull(await this.svc.GetContextAsync(admin));
        await this.svc.SetSuspendedAsync(clinic.Id, suspended: true);
        Assert.Null(await this.svc.GetContextAsync(admin));
        Assert.Equal("suspended", (await this.svc.ListOrganisationsAsync()).Single(o => o.Tenant.Id == clinic.Id).Status);
        await this.svc.SetSuspendedAsync(clinic.Id, suspended: false);
        Assert.NotNull(await this.svc.GetContextAsync(admin));
    }

    [Fact]
    public async Task Health_HasItsTiles_AndTwentyFourHours()
    {
        (IReadOnlyList<HealthTile> tiles, IReadOnlyList<(DateTimeOffset Hour, long Count)> hours) = await this.svc.HealthAsync(this.root, DateTimeOffset.UtcNow);
        Assert.Equal(24, hours.Count);
        Assert.Contains(tiles, t => t.Title == "Delivery queue" && t.Text == "Nothing waiting" && t.Level == "ok");
        Assert.Contains(tiles, t => t.Title == "Disk");
        Assert.Contains(tiles, t => t.Title == "Last good backup");
        // anjal.co.in and other.example were added without the console, so have no key.
        Assert.Contains(tiles, t => t.Title == "DKIM" && t.Level == "warn");
        string summary = MailboxService.SummaryText(tiles, hours, await this.svc.ListOrganisationsAsync());
        Assert.Contains("Delivery queue: Nothing waiting", summary, StringComparison.Ordinal);
        Assert.Contains("never anyone's mail", summary, StringComparison.Ordinal);
    }

    private MailboxRow Person(TenantRow t, string local, string domain, string name) =>
        this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = t.Id,
            LocalPart = local,
            Domain = domain,
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        }).GetAwaiter().GetResult();
}
