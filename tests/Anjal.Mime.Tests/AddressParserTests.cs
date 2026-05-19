namespace Anjal.Mime.Tests;

public class AddressParserTests
{
    [Fact]
    public void Parse_BareAddress()
    {
        var addrs = AddressParser.Parse("alice@example.com");
        Assert.Single(addrs);
        Assert.Equal("alice@example.com", addrs[0].Address);
        Assert.Equal(string.Empty, addrs[0].DisplayName);
    }

    [Fact]
    public void Parse_AngleAddrWithDisplayName()
    {
        var addrs = AddressParser.Parse("Alice Smith <alice@example.com>");
        Assert.Single(addrs);
        Assert.Equal("alice@example.com", addrs[0].Address);
        Assert.Equal("Alice Smith", addrs[0].DisplayName);
    }

    [Fact]
    public void Parse_QuotedDisplayName()
    {
        var addrs = AddressParser.Parse("\"Smith, Alice\" <alice@example.com>");
        Assert.Single(addrs);
        Assert.Equal("Smith, Alice", addrs[0].DisplayName);
        Assert.Equal("alice@example.com", addrs[0].Address);
    }

    [Fact]
    public void Parse_QuotedDisplayName_HandlesCommaInside()
    {
        // The comma inside the quoted string must NOT split into two addresses.
        var addrs = AddressParser.Parse("\"Last, First\" <a@b.com>, second@b.com");
        Assert.Equal(2, addrs.Count);
        Assert.Equal("a@b.com", addrs[0].Address);
        Assert.Equal("Last, First", addrs[0].DisplayName);
        Assert.Equal("second@b.com", addrs[1].Address);
    }

    [Fact]
    public void Parse_MultipleAddresses_Comma()
    {
        var addrs = AddressParser.Parse("alice@example.com, bob@example.com, carol@example.com");
        Assert.Equal(3, addrs.Count);
        Assert.Equal("alice@example.com", addrs[0].Address);
        Assert.Equal("bob@example.com", addrs[1].Address);
        Assert.Equal("carol@example.com", addrs[2].Address);
    }

    [Fact]
    public void Parse_RfcComment_Stripped()
    {
        var addrs = AddressParser.Parse("alice@example.com (Alice Smith)");
        Assert.Single(addrs);
        Assert.Equal("alice@example.com", addrs[0].Address);
    }

    [Fact]
    public void Parse_EncodedWordDisplayName_Decoded()
    {
        var addrs = AddressParser.Parse("=?utf-8?B?QWxpY2U=?= <alice@example.com>");
        Assert.Single(addrs);
        Assert.Equal("Alice", addrs[0].DisplayName);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AddressParser.Parse(null));
        Assert.Empty(AddressParser.Parse(string.Empty));
        Assert.Empty(AddressParser.Parse("   "));
    }

    [Fact]
    public void Parse_MalformedEntry_SkippedRatherThanThrows()
    {
        var addrs = AddressParser.Parse("alice@example.com, not-an-address, bob@example.com");
        Assert.Equal(2, addrs.Count);
        Assert.Equal("alice@example.com", addrs[0].Address);
        Assert.Equal("bob@example.com", addrs[1].Address);
    }

    [Fact]
    public void TryParseOne_Valid_ReturnsTrue()
    {
        bool ok = AddressParser.TryParseOne("alice@example.com", out MailAddress? addr);
        Assert.True(ok);
        Assert.NotNull(addr);
        Assert.Equal("alice@example.com", addr!.Address);
    }

    [Fact]
    public void TryParseOne_Invalid_ReturnsFalse()
    {
        bool ok = AddressParser.TryParseOne("garbage", out MailAddress? addr);
        Assert.False(ok);
        Assert.Null(addr);
    }
}
