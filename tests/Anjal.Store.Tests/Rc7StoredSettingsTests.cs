namespace Anjal.Store.Tests;

/// <summary>v1.0.0-rc.7: settings in the database, trusted senders and greylisting memory.</summary>
public sealed class Rc7StoredSettingsTests
{
    [Theory]
    [InlineData("ANJAL_POSTGRES", true)]
    [InlineData("ANJAL_POSTGRES_ADMIN", true)]
    [InlineData("ANJAL_API_TOKEN", true)]
    [InlineData("ANJAL_KEK", true)]
    [InlineData("ANJAL_SUBMISSION_PASSWORD", true)]
    [InlineData("ANJAL_WEBHOOK_SECRET", true)]
    [InlineData("ANJAL_TLS_DEFAULT_MODE", false)]
    [InlineData("ANJAL_DKIM_KEY_PATH", false)]
    [InlineData("ANJAL_WEBMAIL_KEYS_DIR", false)]
    public void IsSecret_KnowsWhatNeverGoesIntoTheDatabase(string key, bool secret)
    {
        Assert.Equal(secret, StoredSettings.IsSecret(key));
        Assert.Equal(!secret, StoredSettings.IsStorable(key));
    }

    [Fact]
    public void FromEnvironment_KeepsOnlyNonSecretAnjalSettings()
    {
        var env = new System.Collections.Hashtable
        {
            ["ANJAL_BIND"] = "0.0.0.0",
            ["ANJAL_KEK"] = "secret",
            ["ANJAL_POSTGRES"] = "Host=x;Password=y",
            ["ANJAL_EMPTY"] = string.Empty,
            ["PATH"] = "/usr/bin",
        };
        IReadOnlyList<SettingRow> rows = StoredSettings.FromEnvironment(SettingRow.ServerScope, env);
        SettingRow only = Assert.Single(rows);
        Assert.Equal("ANJAL_BIND", only.Key);
        Assert.Equal("import", only.UpdatedBy);
    }

    [Fact]
    public async Task Apply_ImportsOnce_ThenTheDatabaseWins_AndStoredSecretsAreIgnored()
    {
        string key = "ANJAL_RC7TEST_" + System.Guid.NewGuid().ToString("N").ToUpperInvariant();
        var store = new InMemoryMessageStore();
        var log = new List<string>();
        System.Environment.SetEnvironmentVariable(key, "from-env");
        try
        {
            Assert.True(await StoredSettings.ApplyAsync(store, SettingRow.ServerScope, log.Add) > 0);
            SettingRow imported = Assert.Single(await store.ListSettingsAsync(SettingRow.ServerScope), s => s.Key == key);
            Assert.Equal("from-env", imported.Value);

            await store.UpsertSettingAsync(new SettingRow { Scope = SettingRow.ServerScope, Key = key, Value = "from-db", UpdatedBy = "api" });
            await store.UpsertSettingAsync(new SettingRow { Scope = SettingRow.ServerScope, Key = "ANJAL_KEK", Value = "must-not-apply", UpdatedBy = "api" });
            string? kekBefore = System.Environment.GetEnvironmentVariable("ANJAL_KEK");
            await StoredSettings.ApplyAsync(store, SettingRow.ServerScope, log.Add);

            Assert.Equal("from-db", System.Environment.GetEnvironmentVariable(key));
            Assert.Equal(kekBefore, System.Environment.GetEnvironmentVariable("ANJAL_KEK"));
            Assert.Contains(log, l => l.Contains("ignoring stored ANJAL_KEK", System.StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains(key + ": using the database value", System.StringComparison.Ordinal));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task Import_NeverOverwritesAStoredValue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSettingAsync(new SettingRow { Scope = "server", Key = "ANJAL_BIND", Value = "127.0.0.1", UpdatedBy = "api" });
        int inserted = await store.ImportSettingsAsync(new[]
        {
            new SettingRow { Scope = "server", Key = "ANJAL_BIND", Value = "0.0.0.0", UpdatedBy = "import" },
            new SettingRow { Scope = "server", Key = "ANJAL_PORT", Value = "25", UpdatedBy = "import" },
        });
        Assert.Equal(1, inserted);
        IReadOnlyList<SettingRow> all = await store.ListSettingsAsync("server");
        Assert.Equal("127.0.0.1", all.Single(s => s.Key == "ANJAL_BIND").Value);
        Assert.Empty(await store.ListSettingsAsync("webmail"));
    }

    [Fact]
    public async Task TrustedSenders_AreLowerCasedAndPerMailbox()
    {
        var store = new InMemoryMessageStore();
        var a = System.Guid.NewGuid();
        await store.AddTrustedSenderAsync(a, " Doctor@Rediffmail.com ");
        await store.AddTrustedSenderAsync(a, "doctor@rediffmail.com");
        Assert.Equal("doctor@rediffmail.com", Assert.Single(await store.ListTrustedSendersAsync(a)));
        Assert.Empty(await store.ListTrustedSendersAsync(System.Guid.NewGuid()));
        Assert.True(await store.RemoveTrustedSenderAsync(a, "DOCTOR@rediffmail.com"));
        Assert.Empty(await store.ListTrustedSendersAsync(a));
    }

    [Fact]
    public async Task Greylist_ReplaceKeepsExactlyTheGivenRows()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;
        await store.ReplaceGreylistAsync(new[] { new GreylistRow { Key = "a", FirstSeen = now, LastSeen = now, Passed = true } });
        await store.ReplaceGreylistAsync(new[] { new GreylistRow { Key = "b", FirstSeen = now, LastSeen = now, Passed = false } });
        GreylistRow only = Assert.Single(await store.ListGreylistAsync());
        Assert.Equal("b", only.Key);
        Assert.False(only.Passed);
    }
}
