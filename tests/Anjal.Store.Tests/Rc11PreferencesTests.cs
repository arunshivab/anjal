namespace Anjal.Store.Tests;

/// <summary>rc.11: each person's own settings, saved only on their own.</summary>
public class Rc11PreferencesTests
{
    private static readonly string[] ExpectedLanguages = { "en", "ta", "ml", "hi", "mr", "gu" };
    private static readonly string[] ExpectedLayouts = { "three", "focus", "list" };
    private readonly InMemoryMessageStore store = new();

    private async System.Threading.Tasks.Task<MailboxRow> NewMailboxAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "example" });
        return await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "example.test" });
    }

    [Fact]
    public async System.Threading.Tasks.Task NewMailbox_StartsWithTheDefaults()
    {
        MailboxRow m = await this.NewMailboxAsync();
        Assert.Equal("Asia/Kolkata", m.TimeZone);
        Assert.Equal("en", m.Language);
        Assert.Equal("comfortable", m.Density);
        Assert.Equal("three", m.Layout);
        Assert.False(m.RailFolded);
        Assert.False(m.RailChosen);
        Assert.Equal("language", m.DateFormat);
        Assert.Equal("monday", m.WeekStart);
    }

    [Fact]
    public async System.Threading.Tasks.Task Preferences_AreSaved_AndAGeneralMailboxSaveNeverResetsThem()
    {
        MailboxRow m = await this.NewMailboxAsync();
        Assert.True(await this.store.SetMailboxPreferencesAsync(m.Id, new MailboxPreferences
        {
            TimeZone = "Europe/London",
            Language = "ta",
            Density = "Compact",
            Layout = "focus",
            RailFolded = true,
            RailChosen = true,
            DateFormat = "yyyy-mm-dd",
            WeekStart = "sunday",
        }));

        // A general save, such as a new display name, leaves every preference alone.
        m.DisplayName = "Meera Iyer";
        await this.store.UpsertMailboxAsync(m);

        MailboxRow saved = (await this.store.GetMailboxByIdAsync(m.Id))!;
        Assert.Equal("Meera Iyer", saved.DisplayName);
        Assert.Equal("Europe/London", saved.TimeZone);
        Assert.Equal("ta", saved.Language);
        Assert.Equal("compact", saved.Density);
        Assert.Equal("focus", saved.Layout);
        Assert.True(saved.RailFolded);
        Assert.True(saved.RailChosen);
        Assert.Equal("yyyy-mm-dd", saved.DateFormat);
        Assert.Equal("sunday", saved.WeekStart);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownValues_AreSavedAsTheirDefaults()
    {
        MailboxRow m = await this.NewMailboxAsync();
        await this.store.SetMailboxPreferencesAsync(m.Id, new MailboxPreferences
        {
            TimeZone = "Mars/Olympus_Mons",
            Language = "fr",
            Density = "roomy",
            Layout = "two",
            DateFormat = "mm/dd/yy",
            WeekStart = "wednesday",
        });
        MailboxRow saved = (await this.store.GetMailboxByIdAsync(m.Id))!;
        Assert.Equal("Asia/Kolkata", saved.TimeZone);
        Assert.Equal("en", saved.Language);
        Assert.Equal("comfortable", saved.Density);
        Assert.Equal("three", saved.Layout);
        Assert.Equal("language", saved.DateFormat);
        Assert.Equal("monday", saved.WeekStart);
    }

    [Fact]
    public async System.Threading.Tasks.Task AnUnknownMailbox_IsRefused()
    {
        Assert.False(await this.store.SetMailboxPreferencesAsync(System.Guid.NewGuid(), new MailboxPreferences()));
    }

    [Fact]
    public void TheChoices_AreTheOnesTheDesignOffers()
    {
        Assert.Equal(ExpectedLanguages, MailboxPreferences.Languages);
        Assert.Equal(ExpectedLayouts, MailboxPreferences.Layouts);
        Assert.True(MailboxPreferences.IsKnownTimeZone("Asia/Kolkata"));
        Assert.False(MailboxPreferences.IsKnownTimeZone(" "));
    }
}
