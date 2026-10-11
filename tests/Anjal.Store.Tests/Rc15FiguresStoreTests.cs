namespace Anjal.Store.Tests;

/// <summary>rc.15: what the dashboards count - totals by scope, steps through a period, spam scores, organisations, senders.</summary>
public sealed class Rc15FiguresStoreTests
{
    private static readonly System.DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, System.TimeSpan.Zero);
    private static readonly string[] AppSenders = { "notifications@a.test", "payroll@a.test" };

    [Fact]
    public async Task Figures_CountReceivedSentAndJunk_ByScope_HourAndScore()
    {
        var store = new InMemoryMessageStore();
        (TenantRow a, MailboxRow arun) = await Org(store, "a", "arun");
        MailboxRow meera = await store.UpsertMailboxAsync(new MailboxRow { TenantId = a.Id, LocalPart = "meera", Domain = "a.test" });
        (TenantRow _, MailboxRow other) = await Org(store, "b", "someone");

        await Put(store, arun, "INBOX", Noon.AddHours(-3), score: 0);
        await Put(store, arun, "INBOX", Noon.AddHours(-3), score: 2);
        await Put(store, arun, "Junk", Noon.AddHours(-1), score: 7);
        await Put(store, arun, "Sent", Noon.AddHours(-1), score: 0, isChecked: false);
        await Put(store, arun, "Drafts", Noon.AddHours(-1), score: 0, isChecked: false);
        await Put(store, meera, "INBOX", Noon.AddHours(-2), score: 14);
        await Put(store, other, "INBOX", Noon.AddHours(-2), score: 0);
        await Put(store, arun, "INBOX", Noon.AddDays(-3), score: 1);

        MailFigures mine = await store.GetMailFiguresAsync(FigureScope.Mailbox(arun.Id), Noon.AddHours(-12), Noon, "UTC", TimeStep.Hour);
        Assert.Equal(2, mine.Received);
        Assert.Equal(1, mine.Sent);
        Assert.Equal(1, mine.Junk);
        Assert.Equal(3, mine.Checked);
        Assert.Equal(1, mine.Scores[0]);
        Assert.Equal(1, mine.Scores[2]);
        Assert.Equal(1, mine.Scores[7]);
        // Junk is left out of the time chart's received, as of the table's.
        Assert.Equal(new[] { (new System.DateTime(2026, 10, 6, 9, 0, 0), 2L, 0L), (new System.DateTime(2026, 10, 6, 11, 0, 0), 0L, 1L) },
            mine.Series.Select(b => (b.Start, b.Received, b.Sent)).ToArray());

        MailFigures org = await store.GetMailFiguresAsync(FigureScope.Organisation(a.Id), Noon.AddHours(-12), Noon, "UTC", TimeStep.Hour);
        Assert.Equal(3, org.Received);
        Assert.Equal(1, org.Scores[MailFigures.TopScore]);

        MailFigures all = await store.GetMailFiguresAsync(FigureScope.Service, Noon.AddDays(-7), Noon, "Asia/Kolkata", TimeStep.Day);
        Assert.Equal(5, all.Received);
        // Days in the asked zone: 09:00 UTC is 14:30 in India, the same day.
        Assert.Contains(all.Series, b => b.Start == new System.DateTime(2026, 10, 6) && b.Received == 4);
        Assert.Contains(all.Series, b => b.Start == new System.DateTime(2026, 10, 3) && b.Received == 1);

        IReadOnlyDictionary<System.Guid, long> byOrg = await store.CountReceivedByTenantAsync(Noon.AddHours(-12), Noon);
        Assert.Equal(4, byOrg[a.Id]);
        Assert.Single(byOrg.Keys, k => k != a.Id);
    }

    [Theory]
    [InlineData(TimeStep.Hour, "2026-10-08T14:37:00", "2026-10-08T14:00:00")]
    [InlineData(TimeStep.Day, "2026-10-08T14:37:00", "2026-10-08T00:00:00")]
    [InlineData(TimeStep.Week, "2026-10-08T14:37:00", "2026-10-05T00:00:00")]
    [InlineData(TimeStep.Week, "2026-10-04T09:00:00", "2026-09-28T00:00:00")]
    [InlineData(TimeStep.Month, "2026-10-08T14:37:00", "2026-10-01T00:00:00")]
    public void StepStart_IsTheHourDayMondayOrFirstOfTheMonth(TimeStep step, string local, string expected)
    {
        System.DateTime at = System.DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(System.DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), InMemoryMessageStore.StepStart(at, step));
    }

    [Fact]
    public async Task OutboundBySender_CountsSentWaitingAndBounced()
    {
        var store = new InMemoryMessageStore();
        foreach (OutboundStatus status in new[] { OutboundStatus.Sent, OutboundStatus.Sent, OutboundStatus.Pending, OutboundStatus.Failed })
        {
            OutboundMessage m = await store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "Notifications@A.test", EnvelopeTo = "x@y.test", RawBytes = new byte[] { 1 }, GiveUpAt = System.DateTimeOffset.UtcNow.AddDays(1) });
            m.Status = status;
        }
        IReadOnlyList<SenderTraffic> t = await store.CountOutboundBySenderAsync(AppSenders, System.DateTimeOffset.UtcNow.AddHours(-1), System.DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(new SenderTraffic("notifications@a.test", 2, 1, 1), t[0]);
        Assert.Equal(new SenderTraffic("payroll@a.test", 0, 0, 0), t[1]);
    }

    private static async Task<(TenantRow, MailboxRow)> Org(InMemoryMessageStore store, string slug, string local)
    {
        TenantRow t = await store.UpsertTenantAsync(new TenantRow { Slug = slug });
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = t.Id, Domain = slug + ".test" });
        MailboxRow m = await store.UpsertMailboxAsync(new MailboxRow { TenantId = t.Id, LocalPart = local, Domain = slug + ".test" });
        return (t, m);
    }

    private static async Task Put(InMemoryMessageStore store, MailboxRow box, string folder, System.DateTimeOffset at, int score, bool isChecked = true)
    {
        FolderRow f = await store.EnsureFolderAsync(box.Id, folder);
        store.MessageClock = () => at;
        await store.SaveMessageAsync(new MessageRow { MailboxId = box.Id, FolderId = f.Id, MaildirFile = System.Guid.NewGuid().ToString("N"), SpamScore = score, SpamChecked = isChecked });
    }
}
