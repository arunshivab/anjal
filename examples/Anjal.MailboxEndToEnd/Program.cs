using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.MailboxEndToEnd;

/// <summary>
/// Demonstrates the mailbox storage pipeline on localhost. No PostgreSQL
/// or external network needed - everything runs in-process against a
/// temporary Maildir root:
///
///   1. Start an in-memory store and register tenant "imagiqa" with
///      domain "anjal.localhost" and mailbox "arun@anjal.localhost".
///   2. Start an <see cref="SmtpServer"/> on port 0 with a
///      <see cref="MailboxSink"/>.
///   3. Replay an SMTP transaction addressed to the mailbox (with a +tag).
///   4. Show the Maildir file that landed on disk and the index row.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // --- 1. Store, tenant, domain, mailbox ---
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow
        {
            Slug = "imagiqa",
            DisplayName = "imagiQa Healthcare Services",
        });
        await store.UpsertTenantDomainAsync(new TenantDomainRow
        {
            TenantId = tenant.Id,
            Domain = "anjal.localhost",
        });
        MailboxRow mailbox = await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.localhost",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery staple"),
        });

        string root = Path.Combine(Path.GetTempPath(), "anjal-mailbox-demo-" + Guid.NewGuid().ToString("N"));
        var maildir = new MaildirStore(root, "demo");
        Console.WriteLine($"Maildir root: {root}");

        // --- 2. SMTP server with the mailbox sink ---
        void Log(string line) => Console.WriteLine($"[server] {line}");
        var sink = new MailboxSink(store, maildir, Log);

        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "anjal.localhost",
        };
        using var cts = new CancellationTokenSource();
        using var server = new SmtpServer(options, sink);
        Task serverTask = server.StartAsync(cts.Token);
        await Task.Delay(100);  // Brief delay for the listener to bind.
        Console.WriteLine($"SMTP server listening on 127.0.0.1:{server.BoundPort}");

        // --- 3. Replay an SMTP transaction ---
        await ReplayTransactionAsync(server.BoundPort);

        // --- 4. Inspect disk and index ---
        Console.WriteLine();
        Console.WriteLine("=== Maildir on disk ===");
        string inboxNew = Path.Combine(maildir.FolderPath(tenant.Slug, mailbox.Address, FolderRow.Inbox), "new");
        foreach (string file in Directory.GetFiles(inboxNew))
        {
            Console.WriteLine($"  {file} ({new FileInfo(file).Length} bytes)");
        }

        Console.WriteLine();
        Console.WriteLine("=== Index rows ===");
        IReadOnlyList<MessageRow> rows = await store.ListMessagesAsync(mailbox.Id, null, 10, 0);
        foreach (MessageRow r in rows)
        {
            Console.WriteLine($"  id:         {r.Id}");
            Console.WriteLine($"  file:       {r.MaildirFile}");
            Console.WriteLine($"  from:       {r.FromHeader}");
            Console.WriteLine($"  subject:    {r.Subject}");
            Console.WriteLine($"  message-id: {r.MessageId}");
            Console.WriteLine($"  size:       {r.SizeBytes}");
        }
        MailboxRow after = (await store.GetMailboxByIdAsync(mailbox.Id))!;
        Console.WriteLine($"  used bytes: {after.UsedBytes} of {after.QuotaBytes}");

        // Round-trip: read the bytes back through the store path.
        if (rows.Count > 0)
        {
            byte[]? raw = await maildir.ReadAsync(tenant.Slug, mailbox.Address, FolderRow.Inbox, rows[0].MaildirFile);
            Console.WriteLine($"  read back:  {(raw is null ? "MISSING" : raw.Length + " bytes")}");
        }

        // --- 5. Cleanup ---
        cts.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        Directory.Delete(root, recursive: true);

        Console.WriteLine();
        Console.WriteLine(rows.Count == 1 ? "Mailbox end-to-end demo complete." : "ERROR: expected exactly one indexed message.");
        return rows.Count == 1 ? 0 : 1;
    }

    private static async Task ReplayTransactionAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var net = client.GetStream();
        var reader = new StreamReader(net, Encoding.ASCII);
        var writer = new StreamWriter(net, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        async Task<string> ReadReplyAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line is null)
                {
                    return sb.ToString();
                }
                sb.AppendLine(line);
                if (line.Length >= 4 && line[3] == ' ')
                {
                    return sb.ToString();
                }
            }
        }

        async Task SendAsync(string cmd, string? expected = null)
        {
            await writer.WriteLineAsync(cmd);
            string reply = await ReadReplyAsync();
            Console.Write($"[client] > {cmd}\n[client] < {reply}");
            if (expected is not null && !reply.StartsWith(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected reply starting with '{expected}', got: {reply}");
            }
        }

        string banner = await ReadReplyAsync();
        Console.Write($"[client] < {banner}");

        await SendAsync("EHLO test.client", "250");
        await SendAsync("MAIL FROM:<colleague@example.com>", "250");
        await SendAsync("RCPT TO:<arun+newsletters@anjal.localhost>", "250");
        await SendAsync("DATA", "354");

        string body =
            "From: Colleague <colleague@example.com>\r\n" +
            "To: Arun <arun@anjal.localhost>\r\n" +
            "Subject: Welcome to your Anjal mailbox\r\n" +
            "Date: Thu, 17 Sep 2026 10:00:00 +0530\r\n" +
            "Message-ID: <welcome-001@example.com>\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "This message was stored in a Maildir by Anjal.\r\n" +
            ".";
        await writer.WriteAsync(body + "\r\n");
        await writer.FlushAsync();
        string reply = await ReadReplyAsync();
        Console.Write($"[client] < {reply}");

        await SendAsync("QUIT", "221");
    }
}
