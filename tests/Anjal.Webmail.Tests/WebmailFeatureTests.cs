using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public sealed class WebmailFeatureTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-feat-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private TenantRow tenant = new();
    private MailboxRow mailbox = new();

    public WebmailFeatureTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async System.Threading.Tasks.Task SeedAsync()
    {
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" }).ConfigureAwait(false);
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery"),
        }).ConfigureAwait(false);
        await this.svc.ListFoldersAsync(this.mailbox.Id).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string raw)
    {
        var sink = new MailboxSink(this.store, this.maildir);
        await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        }).ConfigureAwait(false);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private const string ThreadMail =
        "From: Dr. Nandini Varma <nandini@apulki.in>\r\nTo: arun@anjal.co.in, physics@apulki.in\r\nCc: records@apulki.in\r\n" +
        "Subject: Discharge summary\r\nDate: Sat, 20 Sep 2026 14:32:00 +0530\r\nMessage-ID: <orig-1@apulki.in>\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n\r\nPhysics review is done.\r\nPlan approved.\r\n";

    // ---------------- drafts ----------------

    [Fact]
    public async System.Threading.Tasks.Task Draft_Save_Reload_ReplaceThenSend_LeavesOneSentAndNoDraft()
    {
        await this.SeedAsync();
        var req = new ComposeRequest { To = "alice@example.com", Subject = "Half written", Body = "One line" };
        System.Guid? first = await this.svc.SaveDraftAsync(this.mailbox.Id, req);
        Assert.NotNull(first);

        FolderRow drafts = (await this.svc.GetFolderAsync(this.mailbox.Id, "Drafts"))!;
        Assert.Equal(1, await this.store.CountMessagesAsync(this.mailbox.Id, drafts.Id));

        ComposeRequest? loaded = await this.svc.LoadDraftAsync(this.mailbox.Id, first!.Value);
        Assert.NotNull(loaded);
        Assert.Equal("alice@example.com", loaded!.To);
        Assert.Equal("Half written", loaded.Subject);
        Assert.Contains("One line", loaded.Body, System.StringComparison.Ordinal);
        Assert.Equal(first, loaded.DraftId);

        // Autosave again: replaces, never accumulates.
        loaded.Body = "One line, then another";
        System.Guid? second = await this.svc.SaveDraftAsync(this.mailbox.Id, loaded);
        Assert.NotEqual(first, second);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.mailbox.Id, drafts.Id));

        loaded.DraftId = second;
        Assert.Null(await this.svc.SendAsync(this.mailbox.Id, loaded));
        Assert.Equal(0, await this.store.CountMessagesAsync(this.mailbox.Id, drafts.Id));
        FolderRow sent = (await this.svc.GetFolderAsync(this.mailbox.Id, "Sent"))!;
        Assert.Equal(1, await this.store.CountMessagesAsync(this.mailbox.Id, sent.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task Draft_OfAnotherMailbox_IsRefused()
    {
        await this.SeedAsync();
        System.Guid? id = await this.svc.SaveDraftAsync(this.mailbox.Id, new ComposeRequest { To = "a@b.c", Subject = "x" });
        MailboxRow other = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "other", Domain = "anjal.co.in" });
        Assert.Null(await this.svc.LoadDraftAsync(other.Id, id!.Value));
    }

    [Fact]
    public async System.Threading.Tasks.Task Draft_OfANonDraftMessage_IsRefused()
    {
        await this.SeedAsync();
        MessageRow delivered = await this.DeliverAsync(ThreadMail);
        Assert.Null(await this.svc.LoadDraftAsync(this.mailbox.Id, delivered.Id));
    }

    // ---------------- reply, reply all, forward ----------------

    [Fact]
    public async System.Threading.Tasks.Task Reply_AddressesTheSender_QuotesWithAttribution()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync(ThreadMail);
        ComposePrefill? p = await this.svc.PrefillAsync(this.mailbox.Id, row.Id, MailboxService.PrefillKind.Reply);

        Assert.NotNull(p);
        Assert.Contains("nandini@apulki.in", p!.To, System.StringComparison.Ordinal);
        Assert.Equal(string.Empty, p.Cc);
        Assert.Equal("Re: Discharge summary", p.Subject);
        Assert.Contains("On 20 September 2026 at", p.Body, System.StringComparison.Ordinal);
        Assert.Contains("> Physics review is done.", p.Body, System.StringComparison.Ordinal);
        Assert.StartsWith("\n\n", p.Body, System.StringComparison.Ordinal);
        Assert.Equal("orig-1@apulki.in", p.InReplyTo);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReplyAll_AddsOthers_ExcludesSelf()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync(ThreadMail);
        ComposePrefill? p = await this.svc.PrefillAsync(this.mailbox.Id, row.Id, MailboxService.PrefillKind.ReplyAll);

        Assert.Contains("nandini@apulki.in", p!.To, System.StringComparison.Ordinal);
        Assert.Contains("physics@apulki.in", p.Cc, System.StringComparison.Ordinal);
        Assert.Contains("records@apulki.in", p.Cc, System.StringComparison.Ordinal);
        Assert.DoesNotContain("arun@anjal.co.in", p.Cc, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task Forward_HasEmptyTo_AndAHeaderBlock()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync(ThreadMail);
        ComposePrefill? p = await this.svc.PrefillAsync(this.mailbox.Id, row.Id, MailboxService.PrefillKind.Forward);

        Assert.Equal(string.Empty, p!.To);
        Assert.Equal("Fwd: Discharge summary", p.Subject);
        Assert.Contains("---------- Forwarded message ----------", p.Body, System.StringComparison.Ordinal);
        Assert.Contains("From: Dr. Nandini Varma", p.Body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("> Physics", p.Body, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Reply_Sent_CarriesInReplyToAndReferences()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync(ThreadMail);
        ComposePrefill p = (await this.svc.PrefillAsync(this.mailbox.Id, row.Id, MailboxService.PrefillKind.Reply))!;
        Assert.Null(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest
        {
            To = p.To,
            Subject = p.Subject,
            Body = "Noted, thank you." + p.Body,
            InReplyTo = p.InReplyTo,
        }));

        var queued = await this.store.LeaseOutboundBatchAsync(5, System.DateTimeOffset.UtcNow.AddMinutes(1));
        MimeMessage sent = MimeParser.Parse(Assert.Single(queued).RawBytes);
        Assert.Equal("<orig-1@apulki.in>", sent.Headers.Get("In-Reply-To"));
        Assert.Equal("<orig-1@apulki.in>", sent.Headers.Get("References"));
    }

    [Fact]
    public void AttributionLine_FallsBackWithoutADate()
    {
        Assert.Equal("Nandini wrote:", MailboxService.AttributionLine("Nandini <n@x.test>", "not a date"));
        Assert.Equal("n@x.test wrote:", MailboxService.AttributionLine("n@x.test", string.Empty));
    }

    // ---------------- Bcc ----------------

    [Fact]
    public async System.Threading.Tasks.Task Bcc_GetsTheMessage_ButIsNotInTheHeaders()
    {
        await this.SeedAsync();
        Assert.Null(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest
        {
            To = "alice@example.com",
            Bcc = "hidden@example.com",
            Subject = "Quiet copy",
            Body = "text",
        }));

        var queued = await this.store.LeaseOutboundBatchAsync(5, System.DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, queued.Count);
        Assert.Contains(queued, m => m.EnvelopeTo == "hidden@example.com");
        MimeMessage parsed = MimeParser.Parse(queued[0].RawBytes);
        Assert.Null(parsed.Headers.Get("Bcc"));
        Assert.DoesNotContain("hidden@example.com", System.Text.Encoding.ASCII.GetString(queued[0].RawBytes), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task Bcc_Invalid_IsRejected()
    {
        await this.SeedAsync();
        Assert.NotNull(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "a@b.c", Bcc = "garbage", Subject = "x" }));
    }

    // ---------------- search ----------------

    [Fact]
    public async System.Threading.Tasks.Task Search_MatchesSubjectFromAndTo_ScopedOrNot()
    {
        await this.SeedAsync();
        await this.DeliverAsync(ThreadMail);
        await this.DeliverAsync("From: Vendor <sales@medsupply.in>\r\nTo: arun@anjal.co.in\r\nSubject: Price list\r\n\r\nbody\r\n");
        MessageRow third = await this.DeliverAsync("From: Lab <lab@example.com>\r\nTo: arun@anjal.co.in\r\nSubject: Reports\r\n\r\nbody\r\n");
        await this.svc.MoveAsync(this.mailbox.Id, third.Id, "Trash");

        FolderRow inbox = (await this.svc.GetFolderAsync(this.mailbox.Id, FolderRow.Inbox))!;
        (var bySubject, long n1) = await this.svc.SearchAsync(this.mailbox.Id, inbox.Id, "discharge", 0);
        Assert.Equal(1, n1);
        Assert.Equal("Discharge summary", bySubject[0].Subject);

        (var byFrom, long n2) = await this.svc.SearchAsync(this.mailbox.Id, inbox.Id, "medsupply", 0);
        Assert.Equal(1, n2);
        Assert.Equal("Price list", byFrom[0].Subject);

        (_, long scoped) = await this.svc.SearchAsync(this.mailbox.Id, inbox.Id, "reports", 0);
        Assert.Equal(0, scoped);
        (_, long all) = await this.svc.SearchAsync(this.mailbox.Id, null, "reports", 0);
        Assert.Equal(1, all);

        (_, long everything) = await this.svc.SearchAsync(this.mailbox.Id, null, string.Empty, 0);
        Assert.Equal(3, everything);
    }

    [Fact]
    public async System.Threading.Tasks.Task Search_DoesNotLeakAnotherMailbox()
    {
        await this.SeedAsync();
        await this.DeliverAsync(ThreadMail);
        MailboxRow other = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "other", Domain = "anjal.co.in" });
        (_, long n) = await this.svc.SearchAsync(other.Id, null, "discharge", 0);
        Assert.Equal(0, n);
    }

    // ---------------- contacts ----------------

    [Fact]
    public async System.Threading.Tasks.Task Contacts_ComeFromSent_MostUsedFirst()
    {
        await this.SeedAsync();
        for (int i = 0; i < 3; i++)
        {
            await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "Alice Roy <alice@example.com>", Subject = "s", Body = "b" });
        }
        await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "bob@example.com", Subject = "s", Body = "b" });

        IReadOnlyList<ContactSuggestion> all = await this.svc.SuggestContactsAsync(this.mailbox.Id, string.Empty);
        Assert.Equal("alice@example.com", all[0].Address);
        Assert.Equal("Alice Roy", all[0].Name);
        Assert.Equal(3, all[0].Count);
        Assert.Equal("bob@example.com", all[1].Address);

        IReadOnlyList<ContactSuggestion> filtered = await this.svc.SuggestContactsAsync(this.mailbox.Id, "bo");
        Assert.Equal("bob@example.com", Assert.Single(filtered).Address);

        IReadOnlyList<ContactSuggestion> byName = await this.svc.SuggestContactsAsync(this.mailbox.Id, "Roy");
        Assert.Single(byName);
    }

    // ---------------- bulk ----------------

    [Fact]
    public async System.Threading.Tasks.Task Bulk_TrashMarkReadUnread_AndMarkAllRead()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync(ThreadMail);
        MessageRow b = await this.DeliverAsync("From: x@y.test\r\nTo: arun@anjal.co.in\r\nSubject: Two\r\n\r\nbody\r\n");
        FolderRow inbox = (await this.svc.GetFolderAsync(this.mailbox.Id, FolderRow.Inbox))!;
        Assert.Equal(2, await this.svc.UnreadInFolderAsync(this.mailbox.Id, inbox.Id));

        Assert.Equal(2, await this.svc.BulkAsync(this.mailbox.Id, new[] { a.Id, b.Id }, MailboxService.BulkAction.MarkRead));
        Assert.Equal(0, await this.svc.UnreadInFolderAsync(this.mailbox.Id, inbox.Id));

        Assert.Equal(1, await this.svc.BulkAsync(this.mailbox.Id, new[] { a.Id }, MailboxService.BulkAction.MarkUnread));
        Assert.Equal(1, await this.svc.UnreadInFolderAsync(this.mailbox.Id, inbox.Id));
        Assert.Equal(1, await this.svc.MarkFolderReadAsync(this.mailbox.Id, inbox.Id));
        Assert.Equal(0, await this.svc.UnreadInFolderAsync(this.mailbox.Id, inbox.Id));

        Assert.Equal(1, await this.svc.BulkAsync(this.mailbox.Id, new[] { b.Id }, MailboxService.BulkAction.Trash));
        FolderRow trash = (await this.svc.GetFolderAsync(this.mailbox.Id, "Trash"))!;
        Assert.Equal(1, await this.store.CountMessagesAsync(this.mailbox.Id, trash.Id));

        // Unknown ids and another mailbox's messages are skipped, not thrown.
        Assert.Equal(0, await this.svc.BulkAsync(this.mailbox.Id, new[] { System.Guid.NewGuid() }, MailboxService.BulkAction.Trash));
    }

    [Fact]
    public async System.Threading.Tasks.Task Bulk_ReportSpamAndNotSpam_CreateSenderRules()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync(ThreadMail);
        Assert.Equal(1, await this.svc.BulkAsync(this.mailbox.Id, new[] { a.Id }, MailboxService.BulkAction.ReportSpam));
        Assert.Empty(await this.store.ListSenderRulesAsync(this.tenant.Id));
        SenderRuleRow blocked = Assert.Single(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));
        Assert.Equal(SenderRuleAction.Block, blocked.Action);

        Assert.Equal(1, await this.svc.BulkAsync(this.mailbox.Id, new[] { a.Id }, MailboxService.BulkAction.NotSpam));
        SenderRuleRow allowed = Assert.Single(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));
        Assert.Equal(SenderRuleAction.Allow, allowed.Action);
    }

    // ---------------- settings ----------------

    [Fact]
    public async System.Threading.Tasks.Task Settings_DisplayName_ThemeAndPassword()
    {
        await this.SeedAsync();
        Assert.Null(await this.svc.SetDisplayNameAsync(this.mailbox.Id, "Dr. Arun Shiva B"));
        Assert.Equal("Dr. Arun Shiva B", (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.DisplayName);
        Assert.NotNull(await this.svc.SetDisplayNameAsync(this.mailbox.Id, new string('x', 101)));

        Assert.Null(await this.svc.SetThemeAsync(this.mailbox.Id, "Midnight"));
        Assert.Equal("midnight", (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.Theme);
        Assert.NotNull(await this.svc.SetThemeAsync(this.mailbox.Id, "neon"));

        // The password survives a display-name change.
        Assert.True(Pbkdf2Hasher.Verify("correct horse battery", (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.PasswordPbkdf2));

        Assert.NotNull(await this.svc.ChangePasswordAsync(this.mailbox.Id, "wrong", "a-long-enough-password", "a-long-enough-password"));
        Assert.NotNull(await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "short", "short"));
        Assert.NotNull(await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "a-long-enough-password", "different-password"));
        Assert.NotNull(await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "correct horse battery", "correct horse battery"));

        Assert.Null(await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "a-long-enough-password", "a-long-enough-password"));
        var auth = new WebmailAuthService(this.store);
        Assert.NotNull(await auth.AuthenticateAsync("arun@anjal.co.in", "a-long-enough-password"));
        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "correct horse battery"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Folders_ReportUnreadSeparatelyFromTotal()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync(ThreadMail);
        await this.DeliverAsync("From: x@y.test\r\nTo: arun@anjal.co.in\r\nSubject: Two\r\n\r\nbody\r\n");
        await this.svc.SetFlagsAsync(this.mailbox.Id, a.Id, seen: true, flagged: false, answered: false);

        IReadOnlyList<FolderView> folders = await this.svc.ListFoldersAsync(this.mailbox.Id);
        FolderView inbox = folders.First(f => f.Name == FolderRow.Inbox);
        Assert.Equal(2, inbox.Count);
        Assert.Equal(1, inbox.Unread);
    }
}

public class ThemeClaimTests
{
    [Fact]
    public void ThemeOf_DefaultsWhenAbsent_AndWithTheme_ReplacesWithoutLosingClaims()
    {
        Assert.Equal("paper", WebmailAuthService.ThemeOf(null));
        Assert.Equal("paper", WebmailAuthService.ThemeOf(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));

        var identity = new System.Security.Claims.ClaimsIdentity("AnjalWebmail");
        System.Guid mailboxId = System.Guid.NewGuid();
        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "arun@anjal.co.in"));
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.MailboxIdClaim, mailboxId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.ThemeClaim, "paper"));
        var user = new System.Security.Claims.ClaimsPrincipal(identity);

        System.Security.Claims.ClaimsPrincipal changed = WebmailAuthService.WithTheme(user, "midnight");
        Assert.Equal("midnight", WebmailAuthService.ThemeOf(changed));
        Assert.Equal(mailboxId, WebmailAuthService.MailboxIdOf(changed));
        Assert.Equal("arun@anjal.co.in", changed.Identity!.Name);
        Assert.Single(changed.Claims, c => c.Type == WebmailAuthService.ThemeClaim);
        Assert.True(changed.Identity.IsAuthenticated);
    }
}
