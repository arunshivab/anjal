using Anjal.Mime;

namespace Anjal.Examples.MimeParse;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("No file specified; parsing built-in sample message.");
            Console.WriteLine();
            byte[] sample = System.Text.Encoding.UTF8.GetBytes(BuiltInSample());
            Render(MimeParser.Parse(sample));
            return 0;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        byte[] raw = File.ReadAllBytes(path);
        Render(MimeParser.Parse(raw));
        return 0;
    }

    private static void Render(MimeMessage msg)
    {
        Console.WriteLine($"From:       {Join(msg.From)}");
        Console.WriteLine($"To:         {Join(msg.To)}");
        Console.WriteLine($"Cc:         {Join(msg.Cc)}");
        Console.WriteLine($"Subject:    {msg.Subject}");
        Console.WriteLine($"Date:       {msg.Date}");
        Console.WriteLine($"Message-ID: {msg.MessageId}");
        Console.WriteLine();
        Console.WriteLine("Structure:");
        RenderEntity(msg.Body, depth: 0);
    }

    private static void RenderEntity(MimeEntity entity, int depth)
    {
        string indent = new(' ', depth * 2);
        Console.WriteLine($"{indent}- {entity.ContentType.MimeType} ({entity.GetType().Name})");
        if (entity is MimePart part)
        {
            Console.WriteLine($"{indent}  encoding: {part.ContentTransferEncoding}");
            Console.WriteLine($"{indent}  bytes:    {part.Body.Length}");
            if (part.ContentType.IsText && part.Body.Length > 0)
            {
                string text = part.GetBodyAsText();
                string preview = text.Length > 80
                    ? string.Concat(text.AsSpan(0, 80), "...")
                    : text;
                preview = preview.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
                Console.WriteLine($"{indent}  preview:  {preview}");
            }
        }
        else if (entity is MimeMultipart multi)
        {
            Console.WriteLine($"{indent}  boundary: {multi.ContentType.Boundary}");
            Console.WriteLine($"{indent}  parts:    {multi.Parts.Count}");
            foreach (var child in multi.Parts)
            {
                RenderEntity(child, depth + 1);
            }
        }
    }

    private static string Join(IReadOnlyList<MailAddress> addrs)
    {
        if (addrs.Count == 0)
        {
            return "(none)";
        }
        return string.Join(", ", addrs);
    }

    private static string BuiltInSample()
    {
        return
            "From: Alice <alice@example.com>\r\n" +
            "To: Bob <bob@example.com>\r\n" +
            "Subject: =?utf-8?B?VGVzdCDinJM=?=\r\n" +
            "Date: Tue, 19 May 2026 12:00:00 +0530\r\n" +
            "Message-ID: <demo-001@anjal.example>\r\n" +
            "Content-Type: multipart/alternative; boundary=ALT\r\n" +
            "\r\n" +
            "--ALT\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hello in plain text.\r\n" +
            "--ALT\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            "<p>Hello in <b>HTML</b> with Caf=C3=A9.</p>\r\n" +
            "--ALT--\r\n";
    }
}
