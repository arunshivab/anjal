namespace Anjal.Mime.Tests;

public class MailAddressTests
{
    [Fact]
    public void Construct_Plain_SetsLocalPartAndDomain()
    {
        var addr = new MailAddress("alice@example.com");
        Assert.Equal("alice", addr.LocalPart);
        Assert.Equal("example.com", addr.Domain);
        Assert.Equal(string.Empty, addr.DisplayName);
    }

    [Fact]
    public void Construct_WithDisplayName_PreservesIt()
    {
        var addr = new MailAddress("alice@example.com", "Alice Smith");
        Assert.Equal("Alice Smith", addr.DisplayName);
    }

    [Fact]
    public void Construct_NoAt_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new MailAddress("no-at-sign"));
    }

    [Fact]
    public void Construct_EmptyLocalPart_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new MailAddress("@example.com"));
    }

    [Fact]
    public void Construct_EmptyDomain_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new MailAddress("alice@"));
    }

    [Fact]
    public void Address_Property_OmitsDisplayName()
    {
        var addr = new MailAddress("alice@example.com", "Alice");
        Assert.Equal("alice@example.com", addr.Address);
    }

    [Fact]
    public void ToString_WithDisplayName_FormatsAsAngleAddr()
    {
        var addr = new MailAddress("alice@example.com", "Alice Smith");
        Assert.Equal("Alice Smith <alice@example.com>", addr.ToString());
    }

    [Fact]
    public void ToString_NoDisplayName_BareAddress()
    {
        var addr = new MailAddress("alice@example.com");
        Assert.Equal("alice@example.com", addr.ToString());
    }

    [Fact]
    public void Equals_DomainIsCaseInsensitive()
    {
        var a = new MailAddress("alice@example.com");
        var b = new MailAddress("alice@EXAMPLE.com");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_LocalPartIsCaseSensitive()
    {
        // RFC 5321 allows case-sensitive local parts; we preserve that.
        var a = new MailAddress("Alice@example.com");
        var b = new MailAddress("alice@example.com");
        Assert.NotEqual(a, b);
    }
}

public class ContentTransferEncodingTests
{
    [Theory]
    [InlineData("7bit", ContentTransferEncoding.SevenBit)]
    [InlineData("8bit", ContentTransferEncoding.EightBit)]
    [InlineData("binary", ContentTransferEncoding.Binary)]
    [InlineData("base64", ContentTransferEncoding.Base64)]
    [InlineData("quoted-printable", ContentTransferEncoding.QuotedPrintable)]
    [InlineData("BASE64", ContentTransferEncoding.Base64)]
    [InlineData("Quoted-Printable", ContentTransferEncoding.QuotedPrintable)]
    public void Parse_CanonicalAndCaseVariants(string input, ContentTransferEncoding expected)
    {
        Assert.Equal(expected, ContentTransferEncodingExtensions.Parse(input));
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsUnknown()
    {
        Assert.Equal(ContentTransferEncoding.Unknown, ContentTransferEncodingExtensions.Parse(null));
        Assert.Equal(ContentTransferEncoding.Unknown, ContentTransferEncodingExtensions.Parse(string.Empty));
        Assert.Equal(ContentTransferEncoding.Unknown, ContentTransferEncodingExtensions.Parse("   "));
    }

    [Fact]
    public void Parse_Unknown_ReturnsUnknown()
    {
        Assert.Equal(ContentTransferEncoding.Unknown, ContentTransferEncodingExtensions.Parse("uuencode"));
    }

    [Fact]
    public void ToHeaderValue_ReturnsCanonicalToken()
    {
        Assert.Equal("base64", ContentTransferEncoding.Base64.ToHeaderValue());
        Assert.Equal("quoted-printable", ContentTransferEncoding.QuotedPrintable.ToHeaderValue());
        Assert.Equal("7bit", ContentTransferEncoding.SevenBit.ToHeaderValue());
    }

    [Fact]
    public void ToHeaderValue_Unknown_DefaultsTo7Bit()
    {
        Assert.Equal("7bit", ContentTransferEncoding.Unknown.ToHeaderValue());
    }
}
