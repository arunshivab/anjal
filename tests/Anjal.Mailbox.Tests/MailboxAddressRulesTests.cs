namespace Anjal.Mailbox.Tests;

/// <summary>DEF-043: what may become a mailbox, checked before anything is written.</summary>
public class MailboxAddressRulesTests
{
    [Theory]
    [InlineData("../evil@qa.test")]
    [InlineData("a/../b@qa.test")]
    [InlineData("<script>@qa.test")]
    [InlineData("a b@qa.test")]
    [InlineData("a\\b@qa.test")]
    [InlineData(".leading@qa.test")]
    [InlineData("trailing.@qa.test")]
    [InlineData("two..dots@qa.test")]
    [InlineData("nul\u0000byte@qa.test")]
    [InlineData("con:@qa.test")]
    [InlineData("arun@qa")]
    [InlineData("arun@.")]
    [InlineData("arun@-bad.test")]
    [InlineData("arun@qa..test")]
    [InlineData("@qa.test")]
    [InlineData("arun@")]
    [InlineData("plain")]
    public void MalformedAddresses_AreRefused(string address)
    {
        Assert.False(MailboxAddressRules.TryValidate(address, out _, out _));
    }

    [Theory]
    [InlineData("arun@qa.test", "arun", "qa.test")]
    [InlineData("arun.shiva@anjal.co.in", "arun.shiva", "anjal.co.in")]
    [InlineData("dr_rao-2@sub.hospital.example", "dr_rao-2", "sub.hospital.example")]
    [InlineData("ARUN@QA.TEST", "arun", "qa.test")]
    public void OrdinaryAddresses_AreAccepted_AndLowercased(string address, string local, string domain)
    {
        Assert.True(MailboxAddressRules.TryValidate(address, out string gotLocal, out string gotDomain));
        Assert.Equal(local, gotLocal);
        Assert.Equal(domain, gotDomain);
    }

    [Fact]
    public void ALocalPartLongerThanSixtyFour_IsRefused()
    {
        Assert.True(MailboxAddressRules.IsValidLocalPart(new string('a', 64)));
        Assert.False(MailboxAddressRules.IsValidLocalPart(new string('a', 65)));
    }
}
