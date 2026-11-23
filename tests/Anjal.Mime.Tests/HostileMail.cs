using System.Text;

namespace Anjal.Mime.Tests;

/// <summary>
/// rc.30 (the coordinator's decision 1 on D-405, 18 Nov): a set of crafted mails, each aimed at one part of the mail
/// reader - the MIME parser, the header and address readers, the encoded-word and transfer decoders, and the HTML
/// cleaner. Every one is made here; none is a real person's mail. Shared with the webmail's tests, which deliver and
/// open each.
/// </summary>
internal static class HostileMail
{
    private static readonly Lazy<List<(string Name, byte[] Raw)>> Set = new(() => Build().ToList());

    /// <summary>Every crafted mail, by a short name (made once, shared by the tests).</summary>
    public static IReadOnlyList<(string Name, byte[] Raw)> All() => Set.Value;

    private static IEnumerable<(string Name, byte[] Raw)> Build()
    {
        yield return ("nested past the limit", Nested(MimeParser.MaxNestingDepth + 1));
        yield return ("nested 100,000 deep", Nested(100_000));
        yield return ("more parts than the limit", ManyParts(MimeParser.MaxParts + 5));
        yield return ("a boundary that never closes", Mail("Content-Type: multipart/mixed; boundary=open\r\n", "--open\r\nContent-Type: text/plain\r\n\r\nno end" + new string('x', 100_000)));
        yield return ("an empty boundary", Mail("Content-Type: multipart/mixed; boundary=\"\"\r\n", "--\r\n\r\nbody\r\n----\r\n"));
        yield return ("a boundary inside its own text", Mail("Content-Type: multipart/mixed; boundary=a\r\n", Repeat("--a\r\n", 20_000)));
        yield return ("a header a megabyte long", Mail("X-Long: " + new string('h', 1024 * 1024) + "\r\n", "body"));
        yield return ("fifty thousand headers", Mail(Repeat("X-H: v\r\n", 50_000), "body"));
        yield return ("a header folded twenty thousand times", Mail("X-Fold: a" + Repeat("\r\n b", 20_000) + "\r\n", "body"));
        yield return ("ten thousand encoded words", Mail(string.Empty, "body", subject: Repeat("=?UTF-8?B?4K6k4K6u4K6/4K604K+N?= ", 10_000)));
        yield return ("encoded words in unknown and broken charsets", Mail(string.Empty, "body", subject: "=?x-no-such?B?AAAA?= =?UTF-8?B?!!!!?= =?UTF-8?Q?=ZZ=?= =?utf-7?B?K0FHRUFad0Jo?= =?UTF-8?X?bad?="));
        yield return ("a Content-Type with ten thousand parameters", Mail("Content-Type: text/plain" + Repeat("; p=v", 10_000) + "\r\n", "body"));
        yield return ("file name continuations out of order", Mail("Content-Type: multipart/mixed; boundary=b\r\n", "--b\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename*999*=x; filename*0*=utf-8''%E0%AE; filename*1=\"..\\..\\..\\etc\\passwd\"; filename*2*=%FF%FE%00\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n--b--\r\n"));
        yield return ("a file name with a right-to-left mark", Mail("Content-Type: multipart/mixed; boundary=b\r\n", "--b\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"report‮fdp.exe\"\r\n\r\nMZ\r\n--b--\r\n"));
        yield return ("base64 full of junk", Mail("Content-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n", "!!!@@@###" + Repeat("QUJD\x01\x02=\r\n", 5_000) + "====="));
        yield return ("quoted-printable broken", Mail("Content-Type: text/plain\r\nContent-Transfer-Encoding: quoted-printable\r\n", Repeat("=\r\n=ZZ=0=", 20_000) + "="));
        yield return ("an unknown transfer encoding", Mail("Content-Type: text/plain\r\nContent-Transfer-Encoding: x-uuencode-of-doom\r\n", "begin 644 x\r\nend\r\n"));
        yield return ("bytes that are not UTF-8, and NULs", Bytes("From: a@b.test\r\nSubject: \xFF\xFE\x00\x80\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n", Enumerable.Range(0, 64_000).Select(i => (byte)(i % 256)).ToArray()));
        yield return ("a body in UTF-7, which .NET refuses", Mail("Content-Type: text/plain; charset=utf-7\r\n", "+AGEAZwBo-"));
        yield return ("a charset that does not exist", Mail("Content-Type: text/plain; charset=\"x-\u0000-nothing\"\r\n", "body"));
        yield return ("ten thousand addresses", Mail(string.Empty, "body", from: string.Join(", ", Enumerable.Range(0, 10_000).Select(i => "u" + i + "@x.test"))));
        yield return ("an address of nested comments", Mail(string.Empty, "body", from: new string('(', 50_000) + "a@b.test" + new string(')', 50_000)));
        yield return ("an address of quotes and angles", Mail(string.Empty, "body", from: Repeat("\"<\\\"<@>", 20_000)));
        yield return ("a date that is not a date", Mail("Date: " + Repeat("Mon, 99 Foo 99999 99:99:99 +9999 ", 2_000) + "\r\n", "body"));
        yield return ("a forwarded mail inside a forwarded mail, 40 deep", Forwarded(40));
        yield return ("HTML nested 100,000 deep", Html(Repeat("<div>", 100_000) + "x" + Repeat("</div>", 100_000)));
        yield return ("HTML that never closes its tags or comments", Html("<!--" + Repeat("<b><i><a href='", 20_000) + "<style>" + Repeat("a{", 20_000)));
        yield return ("HTML with one tag of a hundred thousand attributes", Html("<p " + Repeat("a=b ", 100_000) + ">x</p>"));
        yield return ("HTML with scripts and dangerous links", Html("<script>alert(1)</script><img src=x onerror=alert(2)><a href=\"javascript:alert(3)\">a</a><a href=\"  jAvAsCrIpT:alert(4)\">b</a><iframe src=\"data:text/html,<script>alert(5)</script>\"></iframe><svg><script>alert(6)</script></svg><math><mtext><table><mglyph><style><img src=x onerror=alert(7)>"));
        yield return ("HTML with a style a megabyte long", Html("<p style=\"" + Repeat("background:url(javascript:alert(8));", 30_000) + "\">x</p>"));
        yield return ("HTML with a picture inside it a megabyte long", Html("<img src=\"data:image/png;base64," + new string('A', 1024 * 1024) + "\">"));
    }

    /// <summary>Multipart nested the given number of levels.</summary>
    public static byte[] Nested(int levels)
    {
        var sb = new StringBuilder("From: a@b.test\r\nSubject: nest\r\n");
        for (int i = 0; i < levels; i++)
        {
            sb.Append("Content-Type: multipart/mixed; boundary=\"b").Append(i).Append("x\"\r\n\r\n--b").Append(i).Append("x\r\n");
        }
        sb.Append("Content-Type: text/plain\r\n\r\nleaf\r\n");
        for (int i = levels - 1; i >= 0; i--)
        {
            sb.Append("--b").Append(i).Append("x--\r\n");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] ManyParts(int parts)
    {
        var body = new StringBuilder();
        for (int i = 0; i < parts; i++)
        {
            body.Append("--m\r\nContent-Type: text/plain\r\n\r\n").Append(i).Append("\r\n");
        }
        body.Append("--m--\r\n");
        return Mail("Content-Type: multipart/mixed; boundary=m\r\n", body.ToString());
    }

    private static byte[] Forwarded(int levels)
    {
        string inner = "From: a@b.test\r\nSubject: inner\r\nContent-Type: text/plain\r\n\r\nleaf\r\n";
        for (int i = 0; i < levels; i++)
        {
            inner = "From: a@b.test\r\nSubject: fwd " + i + "\r\nContent-Type: message/rfc822\r\n\r\n" + inner;
        }
        return Encoding.UTF8.GetBytes(inner);
    }

    private static byte[] Html(string html) => Mail("Content-Type: text/html; charset=utf-8\r\n", html);

    private static byte[] Mail(string headers, string body, string subject = "Hostile", string from = "lab@x.test") =>
        Encoding.UTF8.GetBytes("From: " + from + "\r\nTo: arun@anjal.co.in\r\nSubject: " + subject + "\r\nMIME-Version: 1.0\r\n" + headers + "\r\n" + body);

    private static byte[] Bytes(string head, byte[] body) => Encoding.Latin1.GetBytes(head).Concat(body).ToArray();

    private static string Repeat(string s, int times) => new StringBuilder(s.Length * times).Insert(0, s, times).ToString();
}
