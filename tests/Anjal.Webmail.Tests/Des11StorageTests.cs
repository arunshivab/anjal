using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// DES-11 D2 (owner, 9-10 Oct 2026): 1 GB a mailbox by default; each organisation's storage plan -
/// per person, shared, mixed - chosen by the operator; smaller limits from the administrator; the
/// limit enforced (a full mailbox receives nothing and cannot send, except Anjal's security mail);
/// and warnings at 80, 90 and 100%.
/// </summary>
public sealed class Des11StorageTests : IDisposable
{
    private const long MB = 1024L * 1024;

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-des11st-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;

    public Des11StorageTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void TheDefault_Is1GbAMailbox()
    {
        Assert.Equal(1024L * MB, MailboxRow.DefaultQuotaBytes);
        Assert.Equal(1024L * MB, new MailboxRow().QuotaBytes);
        Assert.Equal(1024L * MB, new StoragePlan().OwnLimit(Guid.NewGuid()));
    }

    [Fact]
    public async Task APlan_AppliesAtOnce_LoweringLeavesPeopleOver_AndLimitsCanOnlyBeSmaller()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync(usedRavi: 300 * MB, usedSita: 50 * MB);
        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "person", 2048 * MB, 0));
        Assert.All(await this.store.ListMailboxesAsync(tenant.Id), m => Assert.Equal(2048 * MB, m.QuotaBytes));

        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "person", 200 * MB, 0));
        MailboxRow over = (await this.store.GetMailboxByIdAsync(ravi.Id))!;
        Assert.Equal(200 * MB, over.QuotaBytes);
        Assert.Equal(300 * MB, over.UsedBytes);
        Assert.True((await this.svc.StorageStateAsync(over)).Full);
        Assert.False((await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(sita.Id))!)).Full);

        Assert.NotNull(await this.svc.SetStorageCapAsync(tenant, sita.Id, 500 * MB));
        Assert.Null(await this.svc.SetStorageCapAsync(tenant, sita.Id, 40 * MB));
        Assert.True((await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(sita.Id))!)).Full);
        Assert.Null(await this.svc.SetStorageCapAsync(tenant, sita.Id, 0));
        Assert.Equal(200 * MB, (await this.store.GetMailboxByIdAsync(sita.Id))!.QuotaBytes);
        Assert.NotNull(await this.svc.SaveStoragePlanAsync(tenant.Id, "shared", 0, 1 * MB));
    }

    [Fact]
    public async Task ASharedTotal_FillsForEveryone_AndTheMailServerSays452()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync(usedRavi: 60 * MB, usedSita: 30 * MB);
        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "shared", 0, 100 * MB));
        Assert.Equal(0, (await this.store.GetMailboxByIdAsync(sita.Id))!.QuotaBytes);
        StorageState state = await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(sita.Id))!);
        Assert.False(state.Full);
        Assert.Equal(90, state.Percent);

        await this.store.AddMailboxUsageAsync(ravi.Id, 15 * MB);
        Assert.True((await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(sita.Id))!)).Full);
        PolicyDecision decision = await new QuotaPolicy(this.store).OnRcptToAsync("192.0.2.1", null, "x@example.com", "sita@clinic.example");
        Assert.Equal(452, decision.ReplyCode);
    }

    [Fact]
    public async Task AMixedPlan_DrawsOnTheReserve_OnlyWhenTheOwnSizeIsUsed()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync(usedRavi: 120 * MB, usedSita: 10 * MB);
        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "mixed", 100 * MB, 50 * MB));
        StorageState rs = await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(ravi.Id))!);
        Assert.False(rs.Full);
        Assert.Equal(20 * MB, rs.SharedUsed);
        Assert.Equal(40, rs.Percent);

        await this.store.AddMailboxUsageAsync(ravi.Id, 30 * MB);
        Assert.True((await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(ravi.Id))!)).Full);
        Assert.False((await this.svc.StorageStateAsync((await this.store.GetMailboxByIdAsync(sita.Id))!)).Full);
    }

    [Fact]
    public async Task AFullMailbox_CannotSend_NorReceiveFromColleagues_ButSecurityMailArrives()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync(usedRavi: 0, usedSita: 0);
        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "person", 10 * MB, 0));
        await this.store.AddMailboxUsageAsync(ravi.Id, 10 * MB);

        Assert.Equal(MailboxService.FullToSend, await this.svc.SendAsync(ravi.Id, new ComposeRequest { To = "sita@clinic.example", Subject = "s", Body = "b" }));
        string? toFull = await this.svc.SendAsync(sita.Id, new ComposeRequest { To = "ravi@clinic.example", Subject = "s", Body = "b" });
        Assert.NotNull(toFull);
        Assert.Contains("ravi@clinic.example is full", toFull, StringComparison.Ordinal);

        int before = this.store.MailboxMessages.Count(m => m.MailboxId == ravi.Id);
        Assert.Equal(1, await this.svc.SecurityMailAsync(ravi.Id, "Your password was changed", "It was changed.", "192.0.2.1", "test", Array.Empty<string>()));
        Assert.Equal(before + 1, this.store.MailboxMessages.Count(m => m.MailboxId == ravi.Id));
    }

    [Fact]
    public async Task Warnings_At80_90And100_EachOnce_AndTheAdministratorHearsOfAFullMailbox()
    {
        (TenantRow tenant, MailboxRow ravi, MailboxRow sita) = await this.OrgAsync(usedRavi: 82 * MB, usedSita: 0);
        Assert.Null(await this.svc.SaveStoragePlanAsync(tenant.Id, "person", 100 * MB, 0));
        await this.svc.SetAdminAsync(tenant, sita.Id, true);

        Assert.Equal(1, await this.svc.StorageWarningsAsync());
        Assert.Contains(this.Subjects(ravi.Id), s => s == "Your mailbox is 80% full");
        Assert.Equal(0, await this.svc.StorageWarningsAsync());

        await this.store.AddMailboxUsageAsync(ravi.Id, 18 * MB);
        Assert.Equal(2, await this.svc.StorageWarningsAsync());
        Assert.Contains(this.Subjects(ravi.Id), s => s == "Your mailbox is full");
        Assert.Contains(this.Subjects(sita.Id), s => s == "The mailbox ravi@clinic.example is full");
    }

    [Fact]
    public void NoTextAnywhere_StillSaysTheOld2GbDefault_OrASoftLimit()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var found = new List<string>();
        foreach (string folder in new[] { "src", "deploy", "tools" })
        {
            foreach (string f in Directory.EnumerateFiles(Path.Combine(dir!.FullName, folder), "*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || f.Contains("node_modules", StringComparison.Ordinal)
                    || f.Contains($"{Path.DirectorySeparatorChar}Words{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || !(f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".py", StringComparison.Ordinal) || f.EndsWith(".sh", StringComparison.Ordinal)))
                {
                    continue;
                }
                string text = File.ReadAllText(f);
                foreach (string old in OldSizeWords)
                {
                    if (text.Contains(old, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(Path.GetRelativePath(dir.FullName, f) + ": " + old);
                    }
                }
            }
        }
        string readme = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
        found.AddRange(OldSizeWords.Where(o => readme.Contains(o, StringComparison.OrdinalIgnoreCase)).Select(o => "README.md: " + o));
        Assert.Empty(found);
    }

    private static readonly string[] OldSizeWords = { "2 GiB", "2 GB by default", "2 GB default", "Soft quota", "2147483648", "of 2 GB" };

    private List<string> Subjects(Guid mailbox) => this.store.MailboxMessages.Where(m => m.MailboxId == mailbox).Select(m => m.Subject).ToList();

    private async Task<(TenantRow Tenant, MailboxRow Ravi, MailboxRow Sita)> OrgAsync(long usedRavi, long usedSita)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "clinic", DisplayName = "Clinic" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "clinic.example" });
        MailboxRow ravi = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "ravi", Domain = "clinic.example", DisplayName = "Ravi", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        MailboxRow sita = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "sita", Domain = "clinic.example", DisplayName = "Sita", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        await this.store.AddMailboxUsageAsync(ravi.Id, usedRavi);
        await this.store.AddMailboxUsageAsync(sita.Id, usedSita);
        return (tenant, (await this.store.GetMailboxByIdAsync(ravi.Id))!, (await this.store.GetMailboxByIdAsync(sita.Id))!);
    }
}
