using System.Diagnostics;

namespace Anjal.Mime.Tests;

/// <summary>
/// rc.30 (the coordinator's decision 1 on D-405, 18 Nov): mail bodies and HTML stay in the webmail and the mail server,
/// so every crafted mail must be read - or refused as an ordinary error - quickly, by every reader the webmail uses on
/// it, and never take the process down.
/// </summary>
public class Rc30HostileMailTests
{
    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach ((string name, _) in HostileMail.All())
        {
            names.Add(name);
        }
        return names;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryHostileMail_IsReadOrRefusedPlainly_InTime(string name)
    {
        byte[] raw = HostileMail.All().Single(m => m.Name == name).Raw;
        var clock = Stopwatch.StartNew();
        MimeMessage? message = null;
        try
        {
            message = MimeParser.Parse(raw);
        }
        catch (FormatException)
        {
            // A refusal (MimeParseException is a FormatException): the raw mail is still delivered and kept.
        }
        if (message is not null)
        {
            // Everything the reader asks of a mail.
            _ = message.Subject;
            _ = message.From;
            _ = message.To;
            _ = message.Cc;
            _ = message.Date;
            _ = message.MessageId;
            Walk(message.Body, 0);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{name}: {clock.Elapsed}");
    }

    [Fact]
    public void TheSet_CoversEveryReader()
    {
        string[] names = HostileMail.All().Select(m => m.Name).ToArray();
        Assert.True(names.Length >= 30);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    private static void Walk(MimeEntity entity, int depth)
    {
        Assert.True(depth <= MimeParser.MaxNestingDepth + 1);
        _ = entity.ContentType;
        _ = entity.ContentTransferEncoding;
        if (entity is MimeMultipart multi)
        {
            foreach (MimeEntity part in multi.Parts)
            {
                Walk(part, depth + 1);
            }
        }
        else if (entity is MimePart part)
        {
            _ = part.GetBodyAsText();
        }
    }
}
