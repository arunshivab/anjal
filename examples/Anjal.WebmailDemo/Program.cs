using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;

namespace Anjal.Examples.WebmailDemo;

/// <summary>
/// Runs the Anjal webmail against an in-memory store seeded with one
/// tenant, one mailbox and a few messages, so the UI can be explored in
/// a browser with no PostgreSQL or SMTP setup. The one person is the organisation's
/// administrator, so the organisation screens (Encryption among them) can be seen too:
///
///   dotnet run --project examples/Anjal.WebmailDemo
///   open http://127.0.0.1:8080/  and sign in as arun@anjal.localhost / Lotus-Garden-42
///
/// Messages composed in the UI land in the in-memory outbound queue (and
/// the Sent folder); nothing leaves the machine. Ctrl+C to stop.
/// </summary>
internal static class Program
{
    private static readonly string[] Recipient = new[] { "arun@anjal.localhost" };

    private static async Task<int> Main(string[] args)
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "imagiQa", PostmasterMailbox = "arun@anjal.localhost" });
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.localhost" });
        await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.localhost",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("Lotus-Garden-42"),
        });

        string root = Path.Combine(Path.GetTempPath(), "anjal-webmail-demo-" + Guid.NewGuid().ToString("N"));
        var maildir = new MaildirStore(root, "demo");
        // Score through the real filter (no DNS) so the Junk folder shows a real verdict.
        var sink = new Anjal.Spam.SpamFilterSink(new Anjal.Spam.SpamScorer(), new MailboxSink(store, maildir));

        await DeliverAsync(sink,
            "From: Colleague <colleague@example.com>\r\nTo: arun@anjal.localhost\r\nSubject: Welcome to Anjal webmail\r\n" +
            "Date: Sat, 19 Sep 2026 09:00:00 +0530\r\nMessage-ID: <demo-1@example.com>\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
            "This is a plain-text message.\r\n\r\nTry Flag, Trash and Reply from the toolbar.\r\n");
        await DeliverAsync(sink,
            "From: Newsletter <news@example.com>\r\nTo: arun@anjal.localhost\r\nSubject: HTML message with a tracking pixel\r\n" +
            "Date: Sat, 19 Sep 2026 09:05:00 +0530\r\nMessage-ID: <demo-2@example.com>\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" +
            "<h2>Hello</h2><p>This body is <b>HTML</b>. The script tag is removed and remote images are blocked until you click " +
            "<i>Load images</i>. The first image is a real one (a GitHub avatar); the second is a deliberately non-existent tracking pixel, " +
            "so it stays broken even after loading.</p>" +
            "<script>alert('never runs')</script>" +
            "<p><img src=\"https://github.com/arunshivab.png\" alt=\"GitHub avatar\" width=\"96\" height=\"96\"> " +
            "<img src=\"https://tracker.example/pixel.gif\" alt=\"tracking pixel\"></p>" +
            "<p><a href=\"https://example.com\">A safe link</a></p>\r\n");
        await DeliverAsync(sink,
            "From: Lab <lab@example.com>\r\nTo: arun@anjal.localhost\r\nSubject: Report attached\r\n" +
            "Date: Sat, 19 Sep 2026 09:10:00 +0530\r\nMessage-ID: <demo-3@example.com>\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=X\r\n\r\n--X\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPlease find the report attached.\r\n" +
            "--X\r\nContent-Type: text/csv; name=\"report.csv\"\r\nContent-Disposition: attachment; filename=\"report.csv\"\r\n\r\n" +
            "case,result\r\n18472,normal\r\n--X--\r\n");

        // rc.29 (DES-22): letters whose files open in the mail window - a PDF, a Word file and a workbook; a ZIP whose
        // folder has no entry of its own; and a ZIP locked with a password (the password is in the letter).
        await DeliverAsync(sink, WithFiles("Quality committee <quality@example.com>", "Papers for Friday's review", "Sat, 19 Sep 2026 09:20:00 +0530", "demo-4",
            "The agenda, the October roster and the minutes are attached.",
            ("Agenda-quality-review.pdf", "application/pdf", DemoFiles.Pdf("Quality review - Friday 15:00", "1. Minutes of the last review", "2. Incident reports", "3. Hand hygiene audit")),
            ("Roster-October.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", DemoFiles.Xlsx("October", [["Date", "Ward", "Day", "Night"], ["1 Oct", "Ward 3", "Meera", "Ravi"], ["2 Oct", "Ward 3", "Ravi", "Meera"], ["Shifts", "", "2", "2"]])),
            ("Minutes-30-Sep.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", DemoFiles.Docx("Minutes of the 30 September meeting", ["Present: the board and the quality committee.", "Decided: the audit papers go out by *Friday*; _late papers are not accepted_."], [["Item", "Decision"], ["Audit readiness", "Agreed"]]))));
        await DeliverAsync(sink, WithFiles("External auditor <audit@example.com>", "Documents for the audit", "Sat, 19 Sep 2026 09:25:00 +0530", "demo-5",
            "The audit papers are in the attached ZIP.",
            ("Audit-papers.zip", "application/zip", DemoFiles.Zip(
                ("Audit papers/Infection-control-register.pdf", DemoFiles.Pdf("Infection-control register", "September 2026")),
                ("Audit papers/Calibration-log.xlsx", DemoFiles.Xlsx("Log", [["Device", "Checked"], ["Infusion pump IP-200", "1 Oct"]])),
                ("Training-records.docx", DemoFiles.Docx("Training records", ["Fire safety and hand hygiene, September."], [["Course", "Staff"], ["Fire safety", "42"]]))))));
        await DeliverAsync(sink, WithFiles("Bank alerts <alerts@example.com>", "Account statement for September (password Teak-River-7)", "Sat, 19 Sep 2026 09:30:00 +0530", "demo-6",
            "Your statement is attached, locked with a password. For this demo the password is Teak-River-7.",
            ("Statement-September.zip", "application/zip", DemoFiles.LockedZip("Teak-River-7",
                ("Statement-September.pdf", DemoFiles.Pdf("Account statement", "September 2026")),
                ("Read-me.txt", Encoding.ASCII.GetBytes("Statement for September.\n"))))));

        await DeliverAsync(sink,
            "Subject: YOU HAVE WON THE LOTTERY\r\nContent-Type: text/plain\r\n\r\n" +
            "Dear friend, act now! Click here for a guaranteed risk-free wire transfer of one million dollars.\r\n",
            envelopeFrom: "prize@lottery-winner.test");

        string port = Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_PORT") ?? "8080";
        WebApplication app = Anjal.Webmail.Program.CreateApp(args, store, maildir, "anjal.localhost", $"http://127.0.0.1:{port}");

        // A sent message so the address suggestions and Sent folder have content.
        await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.localhost",
            DisplayName = "Arun",
            Theme = "paper",
        });

        // Seed the tenant's default categories so the dashboard and the
        // category picker have something to show.
        var seedService = new Anjal.Webmail.Services.MailboxService(store, store, maildir, "anjal.localhost");
        await seedService.EnsureTenantDefaultsAsync(tenant.Id);

        Console.WriteLine($"Anjal webmail demo: http://127.0.0.1:{port}/");
        Console.WriteLine("Sign in as   arun@anjal.localhost   password   Lotus-Garden-42");
        Console.WriteLine($"Maildir root: {root}");
        Console.WriteLine("Ctrl+C to stop.");

        await app.RunAsync();
        Directory.Delete(root, recursive: true);
        return 0;
    }

    // A letter with files, as a mail program writes it: the text, then each file in base64.
    private static string WithFiles(string from, string subject, string date, string id, string text, params (string Name, string Type, byte[] Body)[] files)
    {
        var mime = new StringBuilder();
        mime.Append("From: ").Append(from).Append("\r\nTo: arun@anjal.localhost\r\nSubject: ").Append(subject).Append("\r\nDate: ").Append(date)
            .Append("\r\nMessage-ID: <").Append(id).Append("@example.com>\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=X\r\n\r\n")
            .Append("--X\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n").Append(text).Append("\r\n");
        foreach ((string name, string type, byte[] body) in files)
        {
            mime.Append("--X\r\nContent-Type: ").Append(type).Append("; name=\"").Append(name).Append("\"\r\nContent-Disposition: attachment; filename=\"").Append(name)
                .Append("\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Convert.ToBase64String(body, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }
        return mime.Append("--X--\r\n").ToString();
    }

    private static async Task DeliverAsync(Anjal.Spam.SpamFilterSink sink, string raw, string envelopeFrom = "demo@example.com")
    {
        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = Recipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        if (r.Outcome != DeliveryOutcome.Accepted)
        {
            throw new InvalidOperationException("Seed delivery failed: " + r.ReplyText);
        }
    }
}
