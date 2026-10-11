namespace Anjal.Store.Tests;

public class Rc11ActivityZoneTests
{
    private static readonly System.DateTimeOffset At = new(2026, 10, 1, 20, 0, 0, System.TimeSpan.Zero); // 2 Oct 01:30 IST

    [Theory]
    [InlineData("Asia/Kolkata", 2)]
    [InlineData("UTC", 1)]
    [InlineData("Europe/London", 1)]
    [InlineData("Mars/Olympus", 1)]
    public async System.Threading.Tasks.Task AMessageCounts_OnTheDayItArrived_InThePersonsZone(string zone, int expectedDay)
    {
        // DEF-088: the dashboard's days run midnight to midnight in the person's
        // own zone; an unknown zone counts in UTC rather than failing.
        var store = new InMemoryMessageStore { MessageClock = () => At };
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "example" });
        MailboxRow mailbox = await store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "example.test" });
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "m", ReceivedAt = At });

        MailboxActivity activity = await store.GetActivityAsync(mailbox.Id, At.AddDays(-2), At.AddDays(2), zone);

        DailyCount day = Assert.Single(activity.ByDay);
        Assert.Equal(new System.DateTimeOffset(2026, 10, expectedDay, 0, 0, 0, System.TimeSpan.Zero), day.Day);
        Assert.Equal(1, day.Received);
    }
}
