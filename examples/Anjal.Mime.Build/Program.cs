using Anjal.Mime;

namespace Anjal.Examples.MimeBuild;

internal static class Program
{
    private static int Main(string[] args)
    {
        string outputPath = args.Length > 0 ? args[0] : "anjal-sample.eml";

        // Build a multipart/mixed message: a text body plus a binary attachment.
        var multi = MultipartFactory.Create("mixed");

        var textPart = new MimePart();
        textPart.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        textPart.SetBodyAsText(
            "Hello from Anjal!\r\n\r\n" +
            "This message was built programmatically by the Anjal.Mime library.\r\n" +
            "The attachment below contains 32 bytes of arbitrary binary data.\r\n");
        multi.Parts.Add(textPart);

        var attachment = new MimePart();
        attachment.Headers.Add("Content-Type", "application/octet-stream; name=\"sample.bin\"");
        attachment.Headers.Add("Content-Disposition", "attachment; filename=\"sample.bin\"");
        attachment.Headers.Add("Content-Transfer-Encoding", "base64");
        byte[] payload = new byte[32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7);
        }
        attachment.Body = payload;
        multi.Parts.Add(attachment);

        // Wrap in a MimeMessage and add the top-level headers.
        var msg = new MimeMessage(multi);
        msg.Headers.Add("From", "Anjal Demo <demo@anjal.example>");
        msg.Headers.Add("To", "Recipient <recipient@example.com>");
        msg.Subject = "Anjal multipart demo with attachment";
        msg.Date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        msg.Headers.Add("Message-ID", $"<demo-{Guid.NewGuid():N}@anjal.example>");
        msg.Headers.Add("MIME-Version", "1.0");

        byte[] wire = MimeBuilder.Build(msg);
        File.WriteAllBytes(outputPath, wire);

        Console.WriteLine($"Wrote {wire.Length} bytes to {outputPath}");
        Console.WriteLine();
        Console.WriteLine("First 600 bytes of the message:");
        Console.WriteLine(new string('-', 60));
        int preview = Math.Min(600, wire.Length);
        Console.Write(System.Text.Encoding.UTF8.GetString(wire, 0, preview));
        if (wire.Length > preview)
        {
            Console.WriteLine($"... ({wire.Length - preview} more bytes)");
        }
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));

        return 0;
    }
}
