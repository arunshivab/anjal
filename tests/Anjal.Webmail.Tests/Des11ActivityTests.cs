using System.Globalization;
using System.Text.Json;
using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// DES-11 D8 (owner, 10 Oct 2026): sudden rises counted for mail received, sent out, refused and
/// bounced; the database as its own part of the disk bar with its own forecast; and the 30-day
/// activity chart, each day opening that day.
/// </summary>
public sealed class Des11ActivityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-des11a-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MailboxService svc;

    public Des11ActivityTests()
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
    public void ARise_IsOverThreeTimesTheUsualDay_AndAtLeast20()
    {
        Assert.True(MailboxService.IsRise(20, 0));
        Assert.False(MailboxService.IsRise(19, 0));
        Assert.False(MailboxService.IsRise(30, 10));
        Assert.True(MailboxService.IsRise(31, 10));
    }

    [Fact]
    public async Task Rises_AreFoundForSentAndBouncedPerOrganisation_AndForRefusalsOnTheWholeService()
    {
        TenantRow clinic = await this.store.UpsertTenantAsync(new TenantRow { Slug = "clinic", DisplayName = "Clinic" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = clinic.Id, Domain = "clinic.example" });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 30; i++)
        {
            OutboundMessage m = await this.store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "ravi@clinic.example", EnvelopeTo = $"x{i}@example.net", RawBytes = new byte[] { 1 }, CreatedAt = now.AddSeconds(-i - 1) });
            if (i < 25)
            {
                m.Status = OutboundStatus.Failed;
            }
        }
        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string lastWeek = now.ToLocalTime().AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await this.store.SetServiceRecordAsync(MailboxService.RefusalsKind, JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, long>>
        {
            [today] = new() { ["anjal_spam_rejected_total"] = 300 },
            [lastWeek] = new() { ["anjal_spam_rejected_total"] = 70 },
        }));

        IReadOnlyList<SuddenRise> rises = await this.svc.SuddenRisesAsync(ZonedClock.For("UTC", null), now);
        Assert.Contains(rises, r => r.Kind == "sent" && r.Tenant == clinic.Id && r.Today == 30);
        Assert.Contains(rises, r => r.Kind == "bounced" && r.Tenant == clinic.Id && r.Today == 25);
        Assert.Contains(rises, r => r.Kind == "refused" && r.Tenant is null && r.Today == 300);
        Assert.DoesNotContain(rises, r => r.Kind == "received");
    }

    [Fact]
    public async Task Activity_Has30Days_TodayLast_WithBouncedAndRefused()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OutboundMessage m = await this.store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "a@clinic.example", EnvelopeTo = "b@example.net", RawBytes = new byte[] { 1 }, CreatedAt = now.AddMinutes(-1) });
        m.Status = OutboundStatus.Failed;
        IReadOnlyList<ActivityDay> days = await this.svc.ActivityAsync(ZonedClock.For("UTC", null), now);
        Assert.Equal(30, days.Count);
        Assert.Equal(now.UtcDateTime.Date, days[^1].Day);
        Assert.Equal(1, days[^1].Bounced);
        Assert.Equal(0, days[0].Bounced);
    }

    [Fact]
    public void OneDay_OpensFromTheChart_AndOnlyWithinTheLastYear()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        DateTimeOffset now = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);
        DashPeriod day = MailboxService.DashPeriodOf("day:2026-10-05", india, now);
        Assert.Equal("day:2026-10-05", day.Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.Zero), day.Start);
        Assert.Equal(TimeSpan.FromDays(1), day.End - day.Start);
        Assert.Equal("today", MailboxService.DashPeriodOf("day:2020-01-01", india, now).Key);
        Assert.Equal("today", MailboxService.DashPeriodOf("day:2026-12-01", india, now).Key);
    }

    [Fact]
    public void TheDatabase_HasItsOwnForecast()
    {
        var days = new List<DiskDay>();
        for (int i = 0; i < 30; i++)
        {
            days.Add(new DiskDay { Date = new DateTime(2026, 9, 1).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Used = 500L << 30, Database = (10L << 30) + (i * (1L << 30)) });
        }
        var capacity = new ServiceCapacity(1000L << 30, 500L << 30, 0, 0, 0, 0, 0, 39L << 30);
        Assert.Null(MailboxService.MonthsUntilFull(days, capacity));
        Assert.Equal(16, MailboxService.MonthsUntilFull(days, capacity, d => d.Database));
    }
}
