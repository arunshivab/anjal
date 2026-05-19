namespace Anjal.Routing.Tests;

public class AddressResolutionTests
{
    [Fact]
    public void Parse_BareAddress_NoTag()
    {
        AddressResolution? r = AddressResolution.Parse("reports@example.com");
        Assert.NotNull(r);
        Assert.Equal("reports", r.LocalPart);
        Assert.Equal(string.Empty, r.Tag);
        Assert.Equal("example.com", r.Domain);
    }

    [Fact]
    public void Parse_WithTag_ExtractsBoth()
    {
        AddressResolution? r = AddressResolution.Parse("reports+CASE-18472@example.com");
        Assert.NotNull(r);
        Assert.Equal("reports", r.LocalPart);
        Assert.Equal("case-18472", r.Tag);
        Assert.Equal("example.com", r.Domain);
    }

    [Fact]
    public void Parse_LowercasesEverything()
    {
        AddressResolution? r = AddressResolution.Parse("Reports+CASE-X@EXAMPLE.COM");
        Assert.NotNull(r);
        Assert.Equal("reports", r.LocalPart);
        Assert.Equal("case-x", r.Tag);
        Assert.Equal("example.com", r.Domain);
    }

    [Fact]
    public void Parse_EmptyTagAfterPlus_TagIsEmptyString()
    {
        AddressResolution? r = AddressResolution.Parse("reports+@example.com");
        Assert.NotNull(r);
        Assert.Equal("reports", r.LocalPart);
        Assert.Equal(string.Empty, r.Tag);
    }

    [Fact]
    public void Parse_NoAt_ReturnsNull()
    {
        Assert.Null(AddressResolution.Parse("no-at-sign"));
    }

    [Fact]
    public void Parse_EmptyLocalPart_ReturnsNull()
    {
        Assert.Null(AddressResolution.Parse("@example.com"));
    }

    [Fact]
    public void Parse_EmptyDomain_ReturnsNull()
    {
        Assert.Null(AddressResolution.Parse("reports@"));
    }

    [Fact]
    public void Parse_PlusOnlyLocalPart_ReturnsNull()
    {
        // No actual local-part to route to.
        Assert.Null(AddressResolution.Parse("+tag@example.com"));
    }
}
