using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Imap.Tests;

/// <summary>
/// rc.28 (DES-20): Anjal's IMAP server against its own store - sign-in with an app password only (the
/// account password is refused, a removed app password at once), sealed mail opened with the app's own
/// key copy, folders with special uses, stable UIDs, FETCH, STORE, COPY, MOVE, EXPUNGE, APPEND, SEARCH
/// and IDLE.
/// </summary>
public sealed class ImapServerTests : IAsyncLifetime, IDisposable
{
    private static readonly int[] One = { 1 };
    private static readonly int[] Two = { 2 };
    private static readonly string[] SubjectOnly = { "subject" };
    private static readonly long[] Numbers = { 3, 1, 2, 7 };
    private static readonly string[] ToArun = { "arun@anjal.co.in" };
    private static readonly byte[] Pepper = Encoding.ASCII.GetBytes("a test pepper of thirty-two bytes");

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-imap-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private ImapServer server = null!;
    private MailboxRow arun = new();
    private TenantRow tenant = new();
    private string appPassword = string.Empty;
    private Guid appId;

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        this.maildir.Keys = new MailboxKeyDirectory(this.store, this.store);
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        // The organisation's mail is sealed: the person's keys made at their first sign-in, as in the webmail.
        var manager = new MailKeyManager(this.store, this.store, Pepper);
        UnlockedKeys keys = (await manager.CreateMailboxKeysAsync(this.arun, "correct horse battery"))!;
        this.appPassword = AppPasswords.NewPassword();
        AppPasswordRow row = await this.store.AddAppPasswordAsync(AppPasswords.Make(this.arun.Id, "Arun's iPhone", this.appPassword, keys.PrivateKey, Pepper, DateTimeOffset.UtcNow));
        this.appId = row.Id;

        // Two mails in the Inbox, delivered as from outside (sealed on arrival).
        var sink = new MailboxSink(this.store, this.maildir);
        await Deliver(sink, "Rota for next week", "priya@gmail.test", "See you at 10.");
        await Deliver(sink, "Discharge summary", "meena@hospital.test", "Attached.\r\n\r\nRegards");

        this.server = new ImapServer(new ImapServerOptions { BindAddress = IPAddress.Loopback, Port = 0, ImplicitTls = false, IdlePoll = TimeSpan.FromMilliseconds(200) }, new StoreImapBackend(this.store, this.maildir, Pepper));
        this.server.Start();
    }

    public void Dispose()
    {
    }

    public async Task DisposeAsync()
    {
        await this.server.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task TheAccountPassword_IsRefused_AnAppPasswordOpensSealedMail_AndARemovedOneStopsAtOnce()
    {
        await using var c = await Client.ConnectAsync(this.server.LocalPort);
        Assert.Contains("NO [AUTHENTICATIONFAILED] Use a password made for this app", await c.SendAsync("a1 LOGIN arun@anjal.co.in \"correct horse battery\""), StringComparison.Ordinal);
        Assert.Contains("a2 OK", await c.SendAsync("a2 LOGIN arun@anjal.co.in " + this.appPassword), StringComparison.Ordinal);
        string select = await c.SendAsync("a3 SELECT INBOX");
        Assert.Contains("* 2 EXISTS", select, StringComparison.Ordinal);
        Assert.Contains("[UIDVALIDITY ", select, StringComparison.Ordinal);
        string body = await c.SendAsync("a4 FETCH 1 (UID BODY.PEEK[TEXT])");
        Assert.Contains("See you at 10.", body, StringComparison.Ordinal);
        Assert.Single(await this.store.ListAppPasswordsAsync(this.arun.Id), a => a.LastUsedAt is not null);

        // The file on disk is sealed: the app's key copy is what opened it.
        // The folder names are checked one by one, so the test reads the same on Windows (\) as on Linux (/).
        string file = Directory.EnumerateFiles(Path.Combine(this.root, "mail"), "*", SearchOption.AllDirectories)
            .First(f => f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "new" or "cur" || part.Contains("INBOX", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("See you at 10.", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

        Assert.True(await this.store.RemoveAppPasswordAsync(this.arun.Id, this.appId, DateTimeOffset.UtcNow));
        await using var again = await Client.ConnectAsync(this.server.LocalPort);
        Assert.Contains("NO [AUTHENTICATIONFAILED]", await again.SendAsync("b1 LOGIN arun@anjal.co.in " + this.appPassword), StringComparison.Ordinal);
        Assert.Contains("imap.sign-in-refused", (await this.store.ListAuditAsync(50)).Select(e => e.Action));
    }

    [Fact]
    public async Task HostileInput_IsRefused_NotFollowed_AndTheServerGoesOn()
    {
        // Review, 9 Nov: lists nested 50,000 deep before signing in once overflowed the stack.
        await using var c = await Client.ConnectAsync(this.server.LocalPort);
        Assert.Contains("x1 BAD", await c.SendAsync("x1 LOGIN " + new string('(', 50_000)), StringComparison.Ordinal);
        Assert.Contains("x2 OK", await c.SendAsync("x2 CAPABILITY"), StringComparison.Ordinal);
        // Before signing in, a literal larger than a sign-in needs is refused.
        Assert.Contains("NO [TOOBIG]", await c.SendLiteralAsync("x3 LOGIN {100000}", "x"), StringComparison.Ordinal);

        await using var s = await this.SignedInAsync();
        await s.SendAsync("y1 SELECT INBOX");
        Assert.Contains("y2 NO", await s.SendAsync("y2 SEARCH " + string.Concat(Enumerable.Repeat("NOT ", 500)) + "ALL"), StringComparison.Ordinal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("y3 OK", await s.SendAsync("y3 LIST \"\" \"" + new string('*', 400) + "x\""), StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "LIST took " + watch.Elapsed);
        Assert.Contains("y4 OK", await s.SendAsync("y4 NOOP"), StringComparison.Ordinal);

        // A message nested 5,000 deep is shown 32 deep, at once.
        var deep = new StringBuilder("From: a@b.test\r\nSubject: Deep\r\n");
        for (int i = 0; i < 5000; i++)
        {
            deep.Append("Content-Type: multipart/mixed; boundary=\"b").Append(i).Append("z\"\r\n\r\n--b").Append(i).Append("z\r\n");
        }
        deep.Append("Content-Type: text/plain\r\n\r\nbottom\r\n");
        string structure = ImapMime.Parse(Encoding.ASCII.GetBytes(deep.ToString())).Structure(extended: false);
        Assert.True(structure.Length < 20_000, "structure length " + structure.Length);
    }

    [Fact]
    public async Task ASwitchedOffMailbox_CannotSignIn()
    {
        await using var c = await this.SignedInAsync();
        this.arun.Enabled = false;
        await this.store.UpsertMailboxAsync(this.arun);
        await using var again = await Client.ConnectAsync(this.server.LocalPort);
        Assert.Contains("NO [AUTHENTICATIONFAILED]", await again.SendAsync("b1 LOGIN arun@anjal.co.in " + this.appPassword), StringComparison.Ordinal);
        Assert.False((await this.store.GetMailboxByIdAsync(this.arun.Id))!.Enabled);
    }

    [Fact]
    public async Task Folders_HaveTheirSpecialUses_AndHiddenOnesAreNotListed()
    {
        await this.store.EnsureFolderAsync(this.arun.Id, "Scheduled");
        await this.store.EnsureFolderAsync(this.arun.Id, "Patients & Co");
        await using var c = await this.SignedInAsync();
        string list = await c.SendAsync("l1 LIST \"\" \"*\"");
        Assert.Contains("(\\HasNoChildren \\Sent) \"/\" \"Sent\"", list, StringComparison.Ordinal);
        Assert.Contains("\\Junk) \"/\" \"Junk\"", list, StringComparison.Ordinal);
        Assert.Contains("\"INBOX\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Scheduled", list, StringComparison.Ordinal);
        Assert.Contains("\"Patients &- Co\"", list, StringComparison.Ordinal);
        Assert.Equal("நோயாளிகள்", ImapText.DecodeFolder(ImapText.EncodeFolder("நோயாளிகள்")));
        Assert.Contains("* 0 EXISTS", await c.SendAsync("l0 EXAMINE \"Patients &- Co\""), StringComparison.Ordinal);
        Assert.Contains("* STATUS \"INBOX\" (MESSAGES 2 UNSEEN 2", await c.SendAsync("l2 STATUS INBOX (MESSAGES UNSEEN UIDNEXT)"), StringComparison.Ordinal);
        Assert.Contains("l3 OK", await c.SendAsync("l3 CREATE \"Old mail\""), StringComparison.Ordinal);
        Assert.Contains("NO [ALREADYEXISTS]", await c.SendAsync("l4 CREATE \"Old mail\""), StringComparison.Ordinal);
        Assert.Contains("NO [CANNOT]", await c.SendAsync("l5 DELETE Sent"), StringComparison.Ordinal);
        Assert.Contains("l6 OK", await c.SendAsync("l6 DELETE \"Old mail\""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_Store_Search_Copy_Move_Expunge_AndUidsStayTheSame()
    {
        await using var c = await this.SignedInAsync();
        string first = await c.SendAsync("f1 SELECT INBOX");
        string validity = System.Text.RegularExpressions.Regex.Match(first, @"UIDVALIDITY (\d+)").Groups[1].Value;
        string fetch = await c.SendAsync("f2 FETCH 1:* (UID FLAGS RFC822.SIZE ENVELOPE BODYSTRUCTURE INTERNALDATE)");
        Assert.Contains("* 1 FETCH (UID 1 FLAGS ()", fetch, StringComparison.Ordinal);
        Assert.Contains("ENVELOPE (", fetch, StringComparison.Ordinal);
        Assert.Contains("\"Rota for next week\"", fetch, StringComparison.Ordinal);
        Assert.Contains("((NIL NIL \"priya\" \"gmail.test\"))", fetch, StringComparison.Ordinal);
        Assert.Contains("BODYSTRUCTURE (\"text\" \"plain\"", fetch, StringComparison.Ordinal);
        Assert.Contains("f2 OK", fetch, StringComparison.Ordinal);

        // A part of the header, and a partial body.
        Assert.Contains("BODY[HEADER.FIELDS (SUBJECT)] {", await c.SendAsync("f3 UID FETCH 2 BODY.PEEK[HEADER.FIELDS (SUBJECT)]"), StringComparison.Ordinal);
        Assert.Contains("BODY[TEXT]<0> {8}\r\nAttached", await c.SendAsync("f4 UID FETCH 2 BODY.PEEK[TEXT]<0.8>"), StringComparison.Ordinal);

        // Flags reach the store; reading with BODY[] marks it read.
        Assert.Contains("* 1 FETCH (FLAGS (\\Seen \\Flagged))", await c.SendAsync("f5 STORE 1 +FLAGS (\\Seen \\Flagged)"), StringComparison.Ordinal);
        Assert.True((await this.store.ListMessagesAsync(this.arun.Id, null, 10, 0)).Single(m => m.Subject.Contains("Rota", StringComparison.Ordinal) || m.Flagged).Flagged);
        Assert.Contains("FLAGS (\\Seen)", await c.SendAsync("f6 FETCH 2 BODY[]"), StringComparison.Ordinal);

        Assert.Contains("* SEARCH 1", await c.SendAsync("f7 SEARCH FLAGGED"), StringComparison.Ordinal);
        Assert.Contains("* SEARCH 2", await c.SendAsync("f8 UID SEARCH SUBJECT discharge"), StringComparison.Ordinal);
        Assert.Contains("* SEARCH 1 2", await c.SendAsync("f9 SEARCH OR FROM priya FROM meena"), StringComparison.Ordinal);
        Assert.Contains("* SEARCH\r\n", await c.SendAsync("f10 SEARCH UNSEEN"), StringComparison.Ordinal);

        // Copy to Archive keeps the original; move to Trash takes it out.
        string copy = await c.SendAsync("f11 UID COPY 1 Archive");
        Assert.Contains("[COPYUID ", copy, StringComparison.Ordinal);
        string move = await c.SendAsync("f12 UID MOVE 2 Trash");
        Assert.Contains("* 2 EXPUNGE", move, StringComparison.Ordinal);
        Assert.Contains("f12 OK", move, StringComparison.Ordinal);

        // \Deleted and EXPUNGE remove for good.
        await c.SendAsync("f13 STORE 1 +FLAGS.SILENT (\\Deleted)");
        Assert.Contains("* 1 EXPUNGE", await c.SendAsync("f14 EXPUNGE"), StringComparison.Ordinal);

        // A new session sees the same numbers.
        await using var d = await this.SignedInAsync();
        Assert.Contains("UIDVALIDITY " + validity, await d.SendAsync("g0 EXAMINE INBOX"), StringComparison.Ordinal);
        // Archive keeps the UIDVALIDITY its COPYUID gave (each folder has its own, from when it was first opened).
        string archiveValidity = System.Text.RegularExpressions.Regex.Match(copy, @"COPYUID (\d+)").Groups[1].Value;
        Assert.Contains("UIDVALIDITY " + archiveValidity, await d.SendAsync("g1 SELECT Archive"), StringComparison.Ordinal);
        string archived = await d.SendAsync("g2 FETCH 1 (UID)");
        Assert.Contains("* 1 FETCH (UID 1)", archived, StringComparison.Ordinal);
        Assert.Contains("* 1 EXISTS", await d.SendAsync("g3 SELECT Trash"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_KeepsTheMessageSealed_DoesNotDoubleTheSentCopy_AndIdleSeesNewMail()
    {
        await using var c = await this.SignedInAsync();
        string mail = "From: arun@anjal.co.in\r\nTo: priya@gmail.test\r\nSubject: From my phone\r\nMessage-ID: <phone1@anjal.co.in>\r\n\r\nSent from the app.\r\n";
        string appended = await c.SendLiteralAsync("p1 APPEND Sent (\\Seen) {" + Encoding.UTF8.GetByteCount(mail) + "}", mail);
        Assert.Contains("p1 OK [APPENDUID ", appended, StringComparison.Ordinal);
        Assert.Contains("p2 OK", await c.SendLiteralAsync("p2 APPEND Sent (\\Seen) {" + Encoding.UTF8.GetByteCount(mail) + "+}", mail, synchronising: false), StringComparison.Ordinal);
        FolderRow sent = await this.store.EnsureFolderAsync(this.arun.Id, "Sent");
        Assert.Single(await this.store.ListMessagesAsync(this.arun.Id, sent.Id, 10, 0));
        Assert.Contains("NO [TRYCREATE]", await c.SendLiteralAsync("p3 APPEND Nowhere {" + Encoding.UTF8.GetByteCount(mail) + "}", mail), StringComparison.Ordinal);

        await c.SendAsync("p4 SELECT INBOX");
        await c.WriteAsync("p5 IDLE\r\n");
        Assert.StartsWith("+", await c.ReadLineAsync(), StringComparison.Ordinal);
        await Deliver(new MailboxSink(this.store, this.maildir), "A new one", "ravi@gmail.test", "Hello.");
        Assert.Equal("* 3 EXISTS", await c.ReadLineAsync());
        await c.WriteAsync("DONE\r\n");
        Assert.StartsWith("p5 OK", await c.ReadLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mime_Structure_Sections_AndSets()
    {
        byte[] raw = Encoding.ASCII.GetBytes("From: a@b.test\r\nSubject: Hi\r\nContent-Type: multipart/mixed; boundary=\"XX\"\r\n\r\npre\r\n--XX\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n--XX\r\nContent-Type: application/pdf; name=\"r.pdf\"\r\nContent-Disposition: attachment; filename=\"r.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0=\r\n--XX--\r\n");
        ImapMime mime = ImapMime.Parse(raw);
        Assert.Equal("multipart", mime.Type);
        Assert.Equal(2, mime.Children.Count);
        Assert.Equal("Hello", Encoding.ASCII.GetString(mime.PartAt(One)!.BodyBytes()));
        Assert.Equal("JVBERi0=", Encoding.ASCII.GetString(mime.PartAt(Two)!.BodyBytes()));
        string structure = mime.Structure(extended: true);
        Assert.StartsWith("((\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7bit\" 5 1", structure, StringComparison.Ordinal);
        Assert.Contains("(\"attachment\" (\"filename\" \"r.pdf\"))", structure, StringComparison.Ordinal);
        Assert.Contains("\"mixed\"", structure, StringComparison.Ordinal);
        Assert.Equal("Subject: Hi\r\n\r\n", Encoding.ASCII.GetString(mime.HeaderFields(SubjectOnly, not: false)));
        Func<long, bool> set = ImapText.ParseSet("1:3,7,9:*", 12)!;
        Assert.True(set(2) && set(7) && set(12) && !set(5) && !set(8));
        Assert.Equal("1:3,7", ImapText.WriteSet(Numbers));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 2, 0, TimeSpan.FromHours(5.5)), ImapText.ParseDate("09-Nov-2026 14:02:00 +0530"));
        Assert.True(AppPasswords.LooksLikeOne(AppPasswords.NewPassword()));
        Assert.False(AppPasswords.LooksLikeOne("correct horse battery"));
    }

    private async Task<Client> SignedInAsync()
    {
        Client c = await Client.ConnectAsync(this.server.LocalPort);
        Assert.Contains(" OK", await c.SendAsync("s0 LOGIN arun@anjal.co.in " + this.appPassword), StringComparison.Ordinal);
        return c;
    }

    private static async Task Deliver(MailboxSink sink, string subject, string from, string body)
    {
        string raw = $"From: <{from}>\r\nTo: <arun@anjal.co.in>\r\nSubject: {subject}\r\nDate: Mon, 09 Nov 2026 10:00:00 +0530\r\nMessage-ID: <{Guid.NewGuid():N}@test>\r\n\r\n{body}\r\n";
        Anjal.Smtp.DeliveryResult r = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
        {
            EnvelopeFrom = from,
            EnvelopeTo = ToArun,
            RawBytes = Encoding.UTF8.GetBytes(raw),
            TransportTls = "TLS 1.3",
        });
        Assert.Equal(Anjal.Smtp.DeliveryOutcome.Accepted, r.Outcome);
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly StreamReader reader;

        private Client(TcpClient tcp)
        {
            this.tcp = tcp;
            this.stream = tcp.GetStream();
            this.reader = new StreamReader(this.stream, new UTF8Encoding(false));
        }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var c = new Client(tcp);
            Assert.StartsWith("* OK", await c.ReadLineAsync(), StringComparison.Ordinal);
            return c;
        }

        public async Task WriteAsync(string text) => await this.stream.WriteAsync(Encoding.UTF8.GetBytes(text));

        public async Task<string> ReadLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await this.reader.ReadLineAsync(cts.Token) ?? string.Empty;
        }

        public async Task<string> SendAsync(string command)
        {
            await this.WriteAsync(command + "\r\n");
            return await this.UntilTaggedAsync(command[..command.IndexOf(' ', StringComparison.Ordinal)]);
        }

        public async Task<string> SendLiteralAsync(string line, string literal, bool synchronising = true)
        {
            string tag = line[..line.IndexOf(' ', StringComparison.Ordinal)];
            await this.WriteAsync(line + "\r\n");
            if (synchronising)
            {
                string go = await this.ReadLineAsync();
                if (!go.StartsWith('+'))
                {
                    return go;
                }
            }
            await this.WriteAsync(literal + "\r\n");
            return await this.UntilTaggedAsync(tag);
        }

        public async ValueTask DisposeAsync()
        {
            await this.WriteAsync("z LOGOUT\r\n");
            this.reader.Dispose();
            this.tcp.Dispose();
        }

        private async Task<string> UntilTaggedAsync(string tag)
        {
            var sb = new StringBuilder();
            while (true)
            {
                string line = await this.ReadLineAsync();
                sb.Append(line).Append("\r\n");
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    return sb.ToString();
                }
                // A literal in the answer: read it whole.
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"\{(\d+)\}$");
                if (m.Success)
                {
                    char[] buf = new char[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        got += await this.reader.ReadAsync(buf.AsMemory(got));
                    }
                    sb.Append(buf);
                }
            }
        }
    }
}
