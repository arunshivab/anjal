using System.IO.Compression;
using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15: the dashboards' service (periods in the viewer's zone, every step
/// of a chart, "no reply", needs attention), DMARC aggregate reports read
/// from mail (items 31 to 33), and offline mail's choice and copies (item 65 b).
/// </summary>
public sealed class Rc15DashboardTests : IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };
    private static readonly long[] ExpectedCounts = { 1240, 52, 37 };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc15-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private readonly TenantRow tenant;
    private readonly MailboxRow arun;

    public Rc15DashboardTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "mail.anjal.co.in");
        this.tenant = this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).GetAwaiter().GetResult();
        this.arun = this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void Periods_TodayAndYesterdayAreCalendarDaysInTheViewersZone_TheOthersEndNow()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        var now = new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero); // 10:30 in India
        DashPeriod today = MailboxService.DashPeriodOf("today", india, now);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero), today.Start);
        Assert.Equal(now, today.End);
        Assert.Equal(TimeStep.Hour, today.Step);
        DashPeriod yesterday = MailboxService.DashPeriodOf("yesterday", india, now);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.Zero), yesterday.Start);
        Assert.Equal(today.Start, yesterday.End);
        Assert.Equal(TimeStep.Day, MailboxService.DashPeriodOf("7d", india, now).Step);
        Assert.Equal(now.AddDays(-30), MailboxService.DashPeriodOf("30d", india, now).Start);
        Assert.Equal(TimeStep.Week, MailboxService.DashPeriodOf("3m", india, now).Step);
        Assert.Equal(TimeStep.Month, MailboxService.DashPeriodOf("12m", india, now).Step);
        Assert.Equal("today", MailboxService.DashPeriodOf("forever", india, now).Key);
    }

    [Fact]
    public async Task OrgAttention_NamesPeopleStoppedForTries_AndFullMailboxes()
    {
        this.arun.QuotaBytes = 1000;
        this.arun.UsedBytes = 1000;
        await this.store.AppendAuditAsync(new AuditEvent { At = DateTimeOffset.UtcNow, Actor = "arun@anjal.co.in", Action = "webmail.signin.throttled", Subject = "arun@anjal.co.in" });
        await this.store.AppendAuditAsync(new AuditEvent { At = DateTimeOffset.UtcNow.AddDays(-3), Actor = "x", Action = "webmail.signin.throttled", Subject = "arun" });
        var people = new[] { new PersonView(this.arun, true, "active", string.Empty, 0, 0, string.Empty, false) };

        IReadOnlyList<DashLine> lines = await this.svc.OrgAttentionAsync(this.tenant, people);

        DashLine stopped = Assert.Single(lines, l => l.Text == "1 person stopped for too many sign-in tries today");
        Assert.Equal("Arun Shiva B", stopped.Args!["who"]);
        Assert.Single(lines, l => l.Text == "1 mailbox full" && l.Level == "bad");
        Assert.DoesNotContain(lines, l => l.Text.Contains("over 90%", StringComparison.Ordinal));
    }

    [Fact]
    public void Bars_HaveEveryStep_UpToNow()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        var now = new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero);
        var figures = new MailFigures { Series = new[] { new TimeBucket(new DateTime(2026, 10, 6, 9, 0, 0), 3, 1) } };
        IReadOnlyList<ChartBar> today = MailboxService.Bars(figures, MailboxService.DashPeriodOf("today", india, now), india);
        Assert.Equal(11, today.Count); // 00 to 10
        Assert.Equal("00", today[0].Label);
        Assert.Equal(new ChartBar("09", "09:00-10:00", 3, 1, new DateTime(2026, 10, 6, 9, 0, 0), new DateTime(2026, 10, 6, 10, 0, 0)), today[9]);
        Assert.Equal(24, MailboxService.Bars(new MailFigures(), MailboxService.DashPeriodOf("yesterday", india, now), india).Count);
        Assert.Equal(8, MailboxService.Bars(new MailFigures(), MailboxService.DashPeriodOf("7d", india, now), india).Count);
        Assert.Equal(13, MailboxService.Bars(new MailFigures(), MailboxService.DashPeriodOf("12m", india, now), india).Count);
    }

    [Fact]
    public async Task NoReply_CountsOnlyMailToAPersonWithNoAnswerForThreeDays()
    {
        var now = DateTimeOffset.UtcNow;
        await this.SentAsync("Quotation for October", "ravi@supplier.example", "Please send your quotation for the October order by Friday.", now.AddDays(-5));
        await this.SentAsync("Thanks", "ravi@supplier.example", "Thanks!", now.AddDays(-5));
        await this.SentAsync("Newsletter reply", "noreply@lists.example", "Please remove me from this list of announcements, thank you.", now.AddDays(-5));
        await this.SentAsync("Only yesterday", "ravi@supplier.example", "This one was sent only yesterday, so it is too early to chase.", now.AddDays(-1));
        await this.SentAsync("Answered", "meena@clinic.example", "Could you confirm the appointment time for Thursday please?", now.AddDays(-6));
        this.store.MessageClock = () => now.AddDays(-4);
        await this.DeliverAsync("From: Meena <meena@clinic.example>\r\nTo: arun@anjal.co.in\r\nSubject: Re: Answered\r\n\r\nConfirmed, 10:00.\r\n");
        this.store.MessageClock = () => DateTimeOffset.UtcNow;

        Assert.Equal(1, await this.svc.NoReplyCountAsync(this.arun.Id, now));
        IReadOnlyList<DashLine> lines = await this.svc.PersonAttentionAsync(this.arun.Id, ZonedClock.Default, now);
        Assert.Contains(lines, l => l.Text == "1 of your messages has had no reply for 3 days" && l.Href == "/no-reply");
    }

    [Fact]
    public void DmarcReports_ReadPlainGzippedAndZipped_AndRefuseWhatIsNotAReport()
    {
        byte[] xml = Encoding.UTF8.GetBytes(Report("google.com", ("192.0.2.10", 1240, "none", "pass", "pass"), ("198.51.100.7", 37, "reject", "fail", "fail")));
        DmarcReport plain = DmarcReports.Read(xml)!;
        Assert.Equal("google.com", plain.Reporter);
        Assert.Equal("anjal.co.in", plain.Domain);
        Assert.Equal(2, plain.Rows.Count);
        Assert.True(plain.Rows[0].Passed);
        Assert.False(plain.Rows[1].Passed);
        Assert.Equal(1240, plain.Rows[0].Count);

        Assert.Equal(2, DmarcReports.Read(Gzip(xml))!.Rows.Count);
        Assert.Equal(2, DmarcReports.Read(Zip("google.com!anjal.co.in!1!2.xml", xml))!.Rows.Count);

        Assert.Null(DmarcReports.Read(Encoding.UTF8.GetBytes("<html><body>not a report</body></html>")));
        Assert.Null(DmarcReports.Read(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE feedback [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><feedback>&x;</feedback>")));
        Assert.Null(DmarcReports.Read(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x01 }));
        Assert.True(DmarcReports.MayBeReport("google.com!anjal.co.in!1!2.zip", "application/zip"));
        Assert.False(DmarcReports.MayBeReport("photo.jpg", "image/jpeg"));
    }

    [Fact]
    public async Task WhoSendsAs_ReadsTheReportsInTheMailbox_OnceEach()
    {
        string report = Report("google.com", ("192.0.2.10", 1240, "none", "pass", "pass"), ("198.51.100.7", 37, "reject", "fail", "fail"), ("203.0.113.9", 52, "none", "fail", "pass"));
        string raw = "From: noreply-dmarc@google.example\r\nTo: arun@anjal.co.in\r\nSubject: Report domain: anjal.co.in Submitter: google.com Report-ID: 1\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nReport attached.\r\n--b\r\nContent-Type: application/gzip; name=\"r.xml.gz\"\r\n" +
            "Content-Disposition: attachment; filename=\"google.com!anjal.co.in!1!2.xml.gz\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(report)), Base64FormattingOptions.InsertLineBreaks) + "\r\n--b--\r\n";
        await this.DeliverAsync(raw);

        DashPeriod month = MailboxService.DashPeriodOf("30d", ZonedClock.Default, DateTimeOffset.UtcNow);
        (IReadOnlyList<SenderLine> lines, int reports) = await this.svc.WhoSendsAsAsync(this.tenant, month);
        Assert.Equal(1, reports);
        Assert.Equal(ExpectedCounts, lines.Select(l => l.Count).ToArray());
        Assert.Equal("pass", lines[0].Status);
        Assert.Equal("fix", lines[1].Status);
        Assert.Equal("SPF pass, DKIM fails", lines[1].Sub);
        Assert.Equal("blocked", lines[2].Status);
        // Read once: kept by message in the organisation's documents.
        Assert.Single((await this.svc.ReadTenantDocumentAsync<Dictionary<Guid, List<DmarcRow>>>(this.tenant.Id, MailboxService.DmarcKind))!);
    }

    [Fact]
    public async Task OfflineMail_IsTheirChoice_NeverOnASharedComputer_AndTheOrganisationMayForbidIt()
    {
        Assert.Equal((true, false), await this.svc.OfflineMailAsync(this.arun.Id, shared: false));
        await this.svc.SetOfflineMailAsync(this.arun.Id, true);
        Assert.Equal((true, true), await this.svc.OfflineMailAsync(this.arun.Id, shared: false));
        Assert.Equal((false, false), await this.svc.OfflineMailAsync(this.arun.Id, shared: true));
        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { OfflineMail = false }));
        Assert.Equal((false, false), await this.svc.OfflineMailAsync(this.arun.Id, shared: false));
    }

    [Fact]
    public async Task Peek_ShowsAMessage_WithoutMarkingItRead()
    {
        MessageRow m = await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nTo: arun@anjal.co.in\r\nSubject: Results\r\n\r\nYour results are ready.\r\n");
        MessageView view = (await this.svc.PeekAsync(this.arun.Id, m.Id))!;
        Assert.Contains("Your results are ready.", view.BodyHtml, StringComparison.Ordinal);
        Assert.False((await this.store.GetMessageByIdAsync(m.Id))!.Seen);
        await this.svc.OpenAsync(this.arun.Id, m.Id, allowRemoteImages: false);
        Assert.True((await this.store.GetMessageByIdAsync(m.Id))!.Seen);
    }

    private static string Report(string org, params (string Ip, long Count, string Disposition, string Dkim, string Spf)[] rows) =>
        "<?xml version=\"1.0\"?><feedback><report_metadata><org_name>" + org + "</org_name><report_id>1</report_id><date_range><begin>1790467200</begin><end>1790553599</end></date_range></report_metadata>" +
        "<policy_published><domain>anjal.co.in</domain><p>reject</p></policy_published>" +
        string.Concat(rows.Select(r => $"<record><row><source_ip>{r.Ip}</source_ip><count>{r.Count}</count><policy_evaluated><disposition>{r.Disposition}</disposition><dkim>{r.Dkim}</dkim><spf>{r.Spf}</spf></policy_evaluated></row><identifiers><header_from>anjal.co.in</header_from></identifiers></record>")) +
        "</feedback>";

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }
        return ms.ToArray();
    }

    private static byte[] Zip(string name, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return ms.ToArray();
    }

    private async Task<MessageRow> DeliverAsync(string raw)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        return this.store.MailboxMessages[^1];
    }

    private async Task SentAsync(string subject, string to, string body, DateTimeOffset at)
    {
        FolderRow sent = await this.store.EnsureFolderAsync(this.arun.Id, "Sent");
        this.store.MessageClock = () => at;
        await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = this.arun.Id,
            FolderId = sent.Id,
            MaildirFile = Guid.NewGuid().ToString("N"),
            FromHeader = "Arun Shiva B <arun@anjal.co.in>",
            ToHeader = to,
            Subject = subject,
            BodyText = body,
            Preview = MessageRow.PreviewOf(body),
            Seen = true,
        });
        this.store.MessageClock = () => DateTimeOffset.UtcNow;
    }
}
