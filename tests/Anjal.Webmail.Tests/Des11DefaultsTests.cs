using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// DES-11 D7 (owner, 10 Oct 2026): changing an organisation default - the colour, the clock,
/// Trash days, Junk days - asks who should get it: only people joining from today, everyone who
/// has not chosen their own, or everyone ("everyone" never where it would keep mail longer).
/// </summary>
public sealed class Des11DefaultsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-des11d-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MailboxService svc;

    public Des11DefaultsTests()
    {
        this.svc = new MailboxService(this.store, this.store, new MaildirStore(this.root, "test"), "anjal.localhost");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task Colour_FollowersGetIt_OwnChoicesStay_AndLightOrDarkIsKept()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync();
        await this.svc.SetThemeAsync(sita.Id, "violet-light");
        await this.svc.SetThemeAsync(ravi.Id, "anjal-dark");
        Assert.False((await this.svc.GetMailSettingsAsync(ravi.Id)).ThemeChosen);
        Assert.True((await this.svc.GetMailSettingsAsync(sita.Id)).ThemeChosen);

        Assert.Equal(1, await this.svc.ApplyDefaultChangeAsync(tenant, "theme", "anjal", "rose", WhoGets.NotChosen));
        Assert.Equal("rose-dark", (await this.store.GetMailboxByIdAsync(ravi.Id))!.Theme);
        Assert.Equal("violet-light", (await this.store.GetMailboxByIdAsync(sita.Id))!.Theme);
        Assert.True(MailboxService.TakeThemeRefresh(ravi.Id, out string fresh));
        Assert.Equal("rose-dark", fresh);
        Assert.False(MailboxService.TakeThemeRefresh(ravi.Id, out _));

        Assert.Equal(0, await this.svc.ApplyDefaultChangeAsync(tenant, "theme", "rose", "navy", WhoGets.Joining));
        Assert.Equal("rose-dark", (await this.store.GetMailboxByIdAsync(ravi.Id))!.Theme);

        Assert.Equal(2, await this.svc.ApplyDefaultChangeAsync(tenant, "theme", "navy", "gold", WhoGets.Everyone));
        Assert.Equal("gold-light", (await this.store.GetMailboxByIdAsync(sita.Id))!.Theme);
        Assert.False((await this.svc.GetMailSettingsAsync(sita.Id)).ThemeChosen);
    }

    [Fact]
    public async Task Language_FollowersGetIt_OwnChoicesStay_UnlessEveryone()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync();
        var words = new Words(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["ta"] = new Dictionary<string, string> { ["Inbox"] = "\u0B87\u0BA9\u0BCD\u0BAA\u0BBE\u0B95\u0BCD\u0BB8\u0BCD" },
            ["hi"] = new Dictionary<string, string> { ["Inbox"] = "\u0907\u0928\u092C\u0949\u0915\u094D\u0938" },
        });
        Assert.NotNull(await this.svc.SetLanguageAndTimeAsync(sita.Id, "hi", null, null, null, words, false));
        Assert.True((await this.svc.GetMailSettingsAsync(sita.Id)).LanguageChosen);
        Assert.False((await this.svc.GetMailSettingsAsync(ravi.Id)).LanguageChosen);

        Assert.Equal(1, await this.svc.ApplyDefaultChangeAsync(tenant, "language", "en", "ta", WhoGets.NotChosen));
        Assert.Equal("ta", (await this.store.GetMailboxByIdAsync(ravi.Id))!.Language);
        Assert.Equal("hi", (await this.store.GetMailboxByIdAsync(sita.Id))!.Language);
        Assert.True(MailboxService.TakeLanguageRefresh(ravi.Id, out string fresh));
        Assert.Equal("ta", fresh);

        Assert.Equal(0, await this.svc.ApplyDefaultChangeAsync(tenant, "language", "ta", "en", WhoGets.Joining));
        Assert.Equal(2, await this.svc.ApplyDefaultChangeAsync(tenant, "language", "ta", "en", WhoGets.Everyone));
        Assert.Equal("en", (await this.store.GetMailboxByIdAsync(sita.Id))!.Language);
        Assert.False((await this.svc.GetMailSettingsAsync(sita.Id)).LanguageChosen);
    }

    [Fact]
    public async Task Clock_JoiningOnlyKeepsTheOldClockForPeopleHere_EveryoneClearsOwnChoices()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync();
        await this.svc.SetClockAsync(sita.Id, "12");

        Assert.Equal(1, await this.svc.ApplyDefaultChangeAsync(tenant, "clock", "24", "12", WhoGets.Joining));
        Assert.Equal("24", (await this.svc.GetMailSettingsAsync(ravi.Id)).Clock);
        Assert.Equal("12", (await this.svc.GetMailSettingsAsync(sita.Id)).Clock);

        Assert.Equal(2, await this.svc.ApplyDefaultChangeAsync(tenant, "clock", "12", "24", WhoGets.Everyone));
        Assert.Equal(string.Empty, (await this.svc.GetMailSettingsAsync(ravi.Id)).Clock);
        Assert.Equal(string.Empty, (await this.svc.GetMailSettingsAsync(sita.Id)).Clock);
    }

    [Fact]
    public async Task TrashAndJunk_EveryoneOnlyWhenShorter_JoiningKeepsTheOldDays()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync();
        Assert.Null(await this.svc.SetMailSettingsAsync(sita.Id, null, 60));

        Assert.True(MailboxService.WouldLoosen("trash", "30", "60"));
        Assert.False(MailboxService.WouldLoosen("trash", "30", "14"));
        Assert.False(MailboxService.WouldLoosen("clock", "24", "12"));
        Assert.Equal(-1, await this.svc.ApplyDefaultChangeAsync(tenant, "trash", "30", "60", WhoGets.Everyone));
        Assert.True((await this.svc.GetMailSettingsAsync(sita.Id)).TrashChosen);

        Assert.Null(await this.svc.SaveRetentionAsync(tenant, new RetentionPolicy { TrashDays = 14, JunkDays = 30 }));
        Assert.Equal(1, await this.svc.ApplyDefaultChangeAsync(tenant, "trash", "30", "14", WhoGets.Joining));
        Assert.Equal(2, await this.svc.ApplyDefaultChangeAsync(tenant, "junk", "90", "30", WhoGets.Joining));
        TrashRule raviRule = await this.svc.TrashRuleForAsync(ravi.Id);
        Assert.Equal(30, raviRule.Days);
        Assert.Equal(90, raviRule.JunkDays);

        Assert.Equal(4, await this.svc.ApplyDefaultChangeAsync(tenant, "trash", "30", "14", WhoGets.Everyone)
            + await this.svc.ApplyDefaultChangeAsync(tenant, "junk", "90", "30", WhoGets.Everyone));
        raviRule = await this.svc.TrashRuleForAsync(ravi.Id);
        Assert.Equal(14, raviRule.Days);
        Assert.Equal(30, raviRule.JunkDays);
        Assert.Equal(14, (await this.svc.TrashRuleForAsync(sita.Id)).Days);
    }

    private async Task<(TenantRow Tenant, MailboxRow Ravi, MailboxRow Sita)> OrgAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "clinic", DisplayName = "Clinic" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "clinic.example" });
        MailboxRow ravi = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "ravi", Domain = "clinic.example", DisplayName = "Ravi", Theme = "anjal-light", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        MailboxRow sita = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "sita", Domain = "clinic.example", DisplayName = "Sita", Theme = "anjal-light", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        return (tenant, ravi, sita);
    }
}
