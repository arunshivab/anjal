namespace Anjal.Smtp.Tests;

/// <summary>
/// SEC-R2: one password rule, applied everywhere. Owner, 10 Oct 2026 (P1-P8): 15 characters
/// when the password is used alone, 12 with two-step; no rule about kinds of characters; a password
/// made up mostly of guessable words or the person's name is refused; characters counted after
/// Unicode normalisation.
/// </summary>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("ward round tuesday tea")]
    [InlineData("wardroundtuesday")]                    // no capital, no digit, no symbol: fine
    [InlineData("Mri-Slot!42-teal")]
    [InlineData("snail mail on the garden path")]       // contains "mail", but is not mostly "mail"
    public void AGoodPassword_IsAccepted(string password)
    {
        Assert.Null(PasswordPolicy.Check(password, "arun@anjal.co.in", "Arun Shiva"));
    }

    [Fact]
    public void TheLength_Is15Alone_And12WithTwoStep()
    {
        Assert.Equal(12, PasswordPolicy.MinimumLength);
        Assert.Equal(15, PasswordPolicy.AloneLength);
        Assert.Equal(15, PasswordPolicy.LengthFor(twoStep: false));
        Assert.Equal(12, PasswordPolicy.LengthFor(twoStep: true));
        Assert.Contains("at least 15", PasswordPolicy.Check("teal lantern 7"), StringComparison.Ordinal);   // 14
        Assert.Null(PasswordPolicy.Check("teal lantern 79"));                                                  // 15
        Assert.Null(PasswordPolicy.Check("teal lantern", minimumLength: PasswordPolicy.LengthFor(true)));      // 12
        Assert.Contains("at least 12", PasswordPolicy.Check("teal lanter", minimumLength: 8), StringComparison.Ordinal);   // never below 12
    }

    [Fact]
    public void ThereIsNoRuleAboutKindsOfCharacters()
    {
        Assert.Null(PasswordPolicy.Check("quietharbourmorning"));
        Assert.Null(PasswordPolicy.Check("QUIET HARBOUR MORNING"));
        Assert.True(PasswordPolicy.Evaluate("quietharbourmorning").All);
    }

    [Theory]
    [InlineData("Apulki@123456789")]          // the hospital's name and digits
    [InlineData("Anjal@2026-anjal")]
    [InlineData("Hospital#Hospital1")]
    [InlineData("Password!Password")]
    [InlineData("abcdefghijklmnop")]          // a straight run
    [InlineData("Aaaaaaaaaaaaaaaa1!")]
    [InlineData("123456789012345")]
    public void PredictablePasswords_AreRefused(string password)
    {
        Assert.NotNull(PasswordPolicy.Check(password));
    }

    [Theory]
    [InlineData("Arun@Shiva12345", "arun@anjal.co.in", "Arun Shiva")]
    [InlineData("ShivaShiva#2026", "arun@anjal.co.in", "Arun Shiva")]
    public void APasswordMostlyTheOwnersName_IsRefused(string password, string address, string name)
    {
        string? why = PasswordPolicy.Check(password, address, name);
        Assert.NotNull(why);
        Assert.Contains("name", why!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameInsideALongPhrase_IsAccepted()
    {
        Assert.Null(PasswordPolicy.Check("shiva walks to the river at dawn", "arun@anjal.co.in", "Arun Shiva"));
    }

    [Fact]
    public void LengthCountsCharacters_AfterNormalising()
    {
        // Fifteen Tamil letters are fifteen characters; a letter with a separate accent is one
        // character after NFKC; an emoji (two UTF-16 units) is one.
        string tamil = new string('\u0B95', 15);
        Assert.Equal(15, PasswordPolicy.CharacterCount(tamil));
        Assert.Equal(1, PasswordPolicy.CharacterCount("e\u0301"));
        Assert.Equal("\u00E9", PasswordPolicy.Normalize("e\u0301"));
        Assert.Equal(1, PasswordPolicy.CharacterCount("\U0001F600"));   // one emoji, two UTF-16 units
    }

    [Fact]
    public void APasswordTypedInEitherForm_OpensTheSameHash()
    {
        string hash = Pbkdf2Hasher.Hash("caf\u00E9 on the hill top", 1000);
        Assert.True(Pbkdf2Hasher.Verify("cafe\u0301 on the hill top", hash));
        Assert.True(Pbkdf2Hasher.Verify("caf\u00E9 on the hill top", hash));
    }
}
