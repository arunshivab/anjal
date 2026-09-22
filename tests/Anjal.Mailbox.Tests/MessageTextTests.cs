using System.Text;

namespace Anjal.Mailbox.Tests;

/// <summary>DEF-015: the text stored for body search.</summary>
public class MessageTextTests
{
    private static string Of(string raw) => MessageText.Extract(Anjal.Mime.MimeParser.Parse(Encoding.UTF8.GetBytes(raw)));

    [Fact]
    public void PlainText_IsKept_WithWhitespaceCollapsed()
    {
        Assert.Equal("Please find the report. Thanks.", Of("Subject: x\r\nContent-Type: text/plain\r\n\r\nPlease find   the report.\r\n\r\nThanks.\r\n"));
    }

    [Fact]
    public void HtmlOnly_LosesMarkupScriptsAndStyles_ButKeepsTheWords()
    {
        string text = Of("Subject: x\r\nContent-Type: text/html\r\n\r\n<style>p{color:red}</style><p>Discharge &amp; follow-up</p><script>alert(1)</script><b>Tuesday</b>\r\n");
        Assert.Equal("Discharge & follow-up Tuesday", text);
    }

    [Fact]
    public void PlainIsPreferredOverHtml_AndAttachmentsAreNeverIndexed()
    {
        string text = Of("Subject: x\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n" +
            "--b\r\nContent-Type: text/html\r\n\r\n<p>html copy</p>\r\n" +
            "--b\r\nContent-Type: text/plain\r\n\r\nplain copy\r\n" +
            "--b\r\nContent-Type: text/plain; name=\"secret.txt\"\r\nContent-Disposition: attachment; filename=\"secret.txt\"\r\n\r\nattachment words\r\n--b--\r\n");
        Assert.Equal("plain copy", text);
    }

    [Fact]
    public void IndianScripts_WithoutADeclaredCharset_AreNotMangled()
    {
        Assert.Equal("நோயாளி அறிக்கை रिपोर्ट", Of("Subject: x\r\nContent-Type: text/plain\r\n\r\nநோயாளி அறிக்கை रिपोर्ट\r\n"));
    }

    [Fact]
    public void AVeryLongBody_IsCapped()
    {
        string text = Of("Subject: x\r\nContent-Type: text/plain\r\n\r\n" + new string('a', MessageText.MaxChars * 2) + "\r\n");
        Assert.Equal(MessageText.MaxChars, text.Length);
    }

    [Fact]
    public void NothingToIndex_IsEmpty_NotAnError()
    {
        Assert.Equal(string.Empty, MessageText.Extract(null));
    }
}
