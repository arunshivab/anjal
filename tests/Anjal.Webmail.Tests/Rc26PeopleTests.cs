using System.IO.Compression;
using System.Text;
using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.26, the people's side: reputation lists (item 96), the Monday summary's spam and help
/// sections (items 64 and 78), programs and disk images (item 85), mailing lists and referrals (items
/// 73 and 76), rule matches and quiet hours (item 80), snoozing until after leave (item 73) and the
/// inactive clock (item 63). One test or more for each, named after what the person sees.
/// </summary>
public sealed class Rc26PeopleTests
{
    private static readonly string[] SpamCopOnly = { "bl.spamcop.net" };

    private static readonly string[] SpamCopAndBarracuda = { "bl.spamcop.net", "b.barracudacentral.org" };

    private static readonly string[] Languages = { "ta", "ml", "hi", "mr", "gu" };

    private static readonly string[] PrivateAddresses = { "127.0.0.1", "10.1.2.3", "169.254.169.254", "198.18.0.1", "224.0.0.1", "::1", "fd00::1", "64:ff9b::7f00:1", "2002:7f00:1::", "2001:0:4136:e378::1", "::ffff:10.0.0.1", "::7f00:1" };

    private static readonly int[] Reminders = { 60, 30, 15, 7, 3, 1 };

    private static readonly byte[] Pepper = Encoding.ASCII.GetBytes("rc26-test-pepper-0123456789abcdef");

    [Fact]
    public void Item96_SpamCopIsAlwaysAsked_BarracudaOnlyWhenTurnedOn_SpamhausNever()
    {
        Assert.Equal(SpamCopOnly, MailboxService.ReputationLists(null));
        Assert.Equal(SpamCopOnly, MailboxService.ReputationLists("off"));
        Assert.Equal(SpamCopAndBarracuda, MailboxService.ReputationLists(" ON "));
        Assert.DoesNotContain(MailboxService.ReputationLists("on"), z => z.Contains("spamhaus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Item64_TheMondaySummary_CountsSpamWithoutAnyAddressOrSubject()
    {
        var counts = new Dictionary<string, long>
        {
            ["anjal_spam_scored_total"] = 1234,
            ["anjal_mailbox_junked_total"] = 56,
            ["anjal_virus_refused_total"] = 2,
            ["anjal_virus_held_total"] = 1,
        };
        string text = MailboxService.SpamWeekText(counts);
        Assert.Contains("Spam this week", text, StringComparison.Ordinal);
        Assert.Contains("Mails scored: 1,234", text, StringComparison.Ordinal);
        Assert.Contains("Put in Junk: 56", text, StringComparison.Ordinal);
        Assert.Contains("Refused or held for a virus: 3", text, StringComparison.Ordinal);
        Assert.Contains("Delayed by greylisting: 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Item78_TheMondaySummary_ListsTheArticlesAnsweredNoThisWeek_MostFirst()
    {
        var now = new Dictionary<string, int[]>
        {
            ["print"] = new[] { 5, 4 },
            ["away"] = new[] { 1, 9 },
            ["search"] = new[] { 7, 2 },
        };
        var before = new Dictionary<string, int> { ["print"] = 1, ["away"] = 2, ["search"] = 2 };
        string text = MailboxService.HelpNoWeekText(now, before, id => id.ToUpperInvariant());
        int away = text.IndexOf("AWAY (/help/away): 7", StringComparison.Ordinal);
        int print = text.IndexOf("PRINT (/help/print): 3", StringComparison.Ordinal);
        Assert.True(away >= 0 && print > away, text);
        Assert.DoesNotContain("SEARCH", text, StringComparison.Ordinal);
        Assert.Contains("None.", MailboxService.HelpNoWeekText(now, now.ToDictionary(kv => kv.Key, kv => kv.Value[1]), id => id), StringComparison.Ordinal);
    }

    [Fact]
    public void Item85_AProgramIsFoundByItsBytes_WhateverItsNameSays()
    {
        byte[] elf = { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0 };
        Assert.True(AttachmentPolicy.IsProgramContent(elf));
        Assert.True(AttachmentPolicy.IsProgramContent(Encoding.ASCII.GetBytes("#!/bin/sh\necho hi\n")));
        Assert.False(AttachmentPolicy.IsProgramContent(Encoding.ASCII.GetBytes("MZ is just a text here, not a program")));
        Assert.True(AttachmentPolicy.IsProgramContent(Zip(("setup.exe", "x"))));
        Assert.False(AttachmentPolicy.IsProgramContent(Zip(("notes.txt", "x"))));
    }

    [Fact]
    public void Item85_AnOfficeFile_IsNotAnArchiveOfProgramsOrDiskImages()
    {
        byte[] docx = Zip(("[Content_Types].xml", "<Types/>"), ("word/vbaProject.bin", "x"), ("word/document.xml", "<w/>"));
        MimeMessage mail = MimeParser.Parse(MailWith("report.docx", docx));
        Assert.Empty(AttachmentPolicy.ProgramsIn(mail));
        Assert.Empty(AttachmentPolicy.DiskImagesIn(mail));
        Assert.Null(AttachmentPolicy.RefusedInside(mail));
    }

    [Fact]
    public void Item85_MailWrittenInside_IsRefusedWhenItCarriesAProgramOrADiskImage()
    {
        MimeMessage withIso = MimeParser.Parse(MailWith("bundle.zip", Zip(("disk.iso", "x"))));
        Assert.Contains("bundle.zip", AttachmentPolicy.DiskImagesIn(withIso));
        string? reason = AttachmentPolicy.RefusedInside(withIso);
        Assert.NotNull(reason);
        Assert.StartsWith("Programs, scripts and disk images cannot be sent by mail", reason, StringComparison.Ordinal);
        Assert.NotNull(AttachmentPolicy.RefusedInside(MimeParser.Parse(MailWith("run.ps1", Encoding.ASCII.GetBytes("Write-Host hi")))));
    }

    [Fact]
    public void Item73_AListIsKnownByItsListId_ElseByItsUnsubscribeAddress()
    {
        MimeMessage byId = MimeParser.Parse("From: news@shop.in\r\nList-Id: \"Shop news\" <News.Shop.In>\r\nSubject: s\r\n\r\nb\r\n");
        Assert.Equal("news.shop.in", MailboxSink.ListIdentity(byId));
        MimeMessage byUnsubscribe = MimeParser.Parse("From: a@b.in\r\nList-Unsubscribe: <mailto:Leave@B.in?subject=x>, <https://b.in/u>\r\nSubject: s\r\n\r\nb\r\n");
        Assert.Equal("mailto:leave@b.in", MailboxSink.ListIdentity(byUnsubscribe));
        Assert.Equal(string.Empty, MailboxSink.ListIdentity(MimeParser.Parse("From: a@b.in\r\nSubject: s\r\n\r\nb\r\n")));
    }

    [Fact]
    public void Item73_AListsMark_IsDifferentForEachMailbox_AndDoesNotNameTheList()
    {
        Guid one = Guid.NewGuid();
        Guid two = Guid.NewGuid();
        string a = MailboxSink.ListMarkOf(one, "news.shop.in", Pepper);
        Assert.Equal(a, MailboxSink.ListMarkOf(one, "news.shop.in", Pepper));
        Assert.NotEqual(a, MailboxSink.ListMarkOf(two, "news.shop.in", Pepper));
        Assert.DoesNotContain("shop", a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, MailboxSink.ListMarkOf(one, string.Empty, Pepper));
    }

    [Fact]
    public void Item76_AReferral_IsUrgentOrRoutine_AndOtherMailIsNeither()
    {
        string head = "From: dr@sgh.in\r\nSubject: s\r\n";
        Assert.Equal("urgent", MailboxSink.ReferralOf(MimeParser.Parse(head + MailboxSink.ReferralHeader + ": 1\r\n" + MailboxSink.ReferralUrgencyHeader + ": Urgent\r\n\r\nb\r\n")));
        Assert.Equal("routine", MailboxSink.ReferralOf(MimeParser.Parse(head + MailboxSink.ReferralHeader + ": 1\r\n\r\nb\r\n")));
        Assert.Equal(string.Empty, MailboxSink.ReferralOf(MimeParser.Parse(head + MailboxSink.ReferralUrgencyHeader + ": urgent\r\n\r\nb\r\n")));
        Assert.Equal(string.Empty, MailboxSink.ReferralOf(null));
    }

    [Fact]
    public void Item80_ARulesMatches_CountTheLast30DaysOnly()
    {
        Guid rule = Guid.NewGuid();
        var today = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
        string json = "{\"" + rule + "\":{\"2026-10-27\":3,\"2026-09-28\":2,\"2026-09-27\":5}}";
        Assert.Equal(5, MailboxSink.RuleMatches(json, rule, today));
        Assert.Equal(0, MailboxSink.RuleMatches(json, Guid.NewGuid(), today));
        Assert.Equal(0, MailboxSink.RuleMatches("not json", rule, today));
        Assert.Equal(0, MailboxSink.RuleMatches(null, rule, today));
    }

    [Fact]
    public void Item80_QuietHours_MayCrossMidnight_AndEqualTimesMeanNone()
    {
        Assert.True(MailboxService.InQuietHours("22:00", "07:00", new TimeOnly(23, 30)));
        Assert.True(MailboxService.InQuietHours("22:00", "07:00", new TimeOnly(6, 59)));
        Assert.False(MailboxService.InQuietHours("22:00", "07:00", new TimeOnly(7, 0)));
        Assert.True(MailboxService.InQuietHours("13:00", "14:00", new TimeOnly(13, 15)));
        Assert.False(MailboxService.InQuietHours("13:00", "14:00", new TimeOnly(14, 15)));
        Assert.False(MailboxService.InQuietHours("09:00", "09:00", new TimeOnly(9, 0)));
        Assert.False(MailboxService.InQuietHours("9 pm", "07:00", new TimeOnly(23, 0)));
    }

    [Fact]
    public void Item73_SnoozeAfterMyLeave_IsTheFirstWorkingMorningAfterTheAutomaticReplyEnds()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        var now = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(5.5));
        // Ends Friday 30 Oct, 18:00 - the next working morning is Monday 2 Nov, 09:00.
        var friday = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.SnoozeTime(india, now, "leave", friday));
        // Ends Wednesday at 08:00 - the same morning.
        var early = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.AfterLeave(india, early));
        // No end, or an end already past: "after my leave" is not offered.
        Assert.Null(MailboxService.SnoozeTime(india, now, "leave", null));
        Assert.Null(MailboxService.SnoozeTime(india, now, "leave", now.AddHours(-1)));
    }

    [Fact]
    public void Item63_AMailboxIsInactiveAfter180DaysAndDeleted186DaysLater_WithRemindersBefore()
    {
        Assert.Equal(TimeSpan.FromDays(180), InactiveAccounts.InactiveAfter);
        Assert.Equal(TimeSpan.FromDays(186), InactiveAccounts.DeletedAfter);
        Assert.Equal(Reminders, InactiveAccounts.ReminderDays);
        Assert.Contains("ask the person for another address", InactiveAccounts.RefusalText, StringComparison.Ordinal);
    }

    [Fact]
    public void Item99_TheDayIsAnjalsOwnDay_AndTheReplySaysTheFigure()
    {
        var late = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset start = SendingDay.DayStart(late);
        Assert.True(start <= late && late - start < TimeSpan.FromDays(1));
        Assert.Equal("Your organisation has sent its 2,000 messages for today; try again tomorrow", SendingDay.OverText(SendingDay.DefaultLimit));
        var state = new SendingDayState(2000, 2500);
        Assert.Equal(0, state.Left);
        Assert.Equal(125, state.Percent);
    }

    [Fact]
    public void Item78_EveryHelpArticle_HasADraftInEachLanguage_WithTheSameSteps()
    {
        HelpGuide guide = HelpGuide.Load();
        foreach (string language in Languages)
        {
            Assert.Contains(language, guide.DraftLanguages);
            foreach (HelpArticle en in guide.Articles)
            {
                HelpArticle? draft = guide.Draft(en.Id, language);
                Assert.NotNull(draft);
                Assert.Equal(en.Steps.Count, draft.Steps.Count);
                Assert.NotEqual(en.Title, draft.Title);
            }
        }
        Assert.Null(guide.Draft("sign-in", "fr"));
    }

    [Fact]
    public void Review_WebhookAddressesThatReachInside_ArePrivate()
    {
        foreach (string a in PrivateAddresses)
        {
            Assert.True(MailboxService.IsPrivate(System.Net.IPAddress.Parse(a)), a);
        }
        Assert.False(MailboxService.IsPrivate(System.Net.IPAddress.Parse("151.185.43.171")));
        Assert.False(MailboxService.IsPrivate(System.Net.IPAddress.Parse("2606:4700::1111")));
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                using Stream s = zip.CreateEntry(name).Open();
                byte[] b = Encoding.UTF8.GetBytes(text);
                s.Write(b, 0, b.Length);
            }
        }
        return ms.ToArray();
    }

    private static string MailWith(string fileName, byte[] file) =>
        "From: a@sgh.in\r\nTo: b@sgh.in\r\nSubject: s\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n"
        + "--b1\r\nContent-Type: text/plain\r\n\r\nhello\r\n"
        + "--b1\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"" + fileName + "\"\r\nContent-Transfer-Encoding: base64\r\n\r\n"
        + Convert.ToBase64String(file, Base64FormattingOptions.InsertLineBreaks) + "\r\n--b1--\r\n";
}
