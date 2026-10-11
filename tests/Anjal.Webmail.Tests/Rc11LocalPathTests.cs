using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class Rc11LocalPathTests
{
    [Theory]
    [InlineData("/folder/Sent", "/folder/Sent")]
    [InlineData("/folder/INBOX?page=2", "/folder/INBOX?page=2")]
    [InlineData("/message/0b0d?images=1", "/message/0b0d?images=1")]
    [InlineData("//evil.example/steal", "/folder/INBOX")]
    [InlineData("/\\evil.example", "/folder/INBOX")]
    [InlineData("https://evil.example", "/folder/INBOX")]
    [InlineData("folder/Sent", "/folder/INBOX")]
    [InlineData("/folder/\nSet-Cookie", "/folder/INBOX")]
    [InlineData("", "/folder/INBOX")]
    [InlineData(null, "/folder/INBOX")]
    public void OnlyAPageOnThisSite_IsReturnedTo(string? back, string expected)
    {
        Assert.Equal(expected, LocalPath.Safe(back));
    }
}
