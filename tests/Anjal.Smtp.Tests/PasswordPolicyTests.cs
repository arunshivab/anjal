namespace Anjal.Smtp.Tests;

/// <summary>SEC-R2: one password rule, applied everywhere.</summary>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery staple")]   // long, no classes: accepted on length
    [InlineData("ward round tuesday tea")]
    [InlineData("Ward#Round7")]
    [InlineData("Mri-Slot!42")]
    [InlineData("k9$Thrum,vale")]
    public void AGoodPassword_IsAccepted(string password)
    {
        Assert.Null(PasswordPolicy.Check(password, "arun@anjal.co.in", "Arun Shiva"));
    }

    [Theory]
    [InlineData("Ab1!567", "at least 8")]                 // too short
    [InlineData("wardround7!", "upper-case")]             // no capital, and under 16
    [InlineData("WARDROUND7!", "lower-case")]             // no lower case
    [InlineData("WardRound!", "number")]                  // no digit
    [InlineData("WardRound77", "symbol")]                 // no symbol
    public void TheCompositionRule_IsEnforced(string password, string because)
    {
        string? why = PasswordPolicy.Check(password);
        Assert.NotNull(why);
        Assert.Contains(because, why!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Passw0rd!")]          // the classic
    [InlineData("Apulki@123")]         // satisfies every class, guessed first
    [InlineData("Anjal@2026")]
    [InlineData("Hospital#1")]
    [InlineData("Abcdefg1!")]          // a straight run
    [InlineData("Aaaaaaa1!")]
    public void PredictablePasswords_AreRefused_EvenThoughTheyPassTheClasses(string password)
    {
        Assert.NotNull(PasswordPolicy.Check(password));
    }

    [Theory]
    [InlineData("Arun@Shiva1", "arun@anjal.co.in", "Arun Shiva")]
    [InlineData("Shiva#2026a", "arun@anjal.co.in", "Arun Shiva")]
    public void APasswordContainingTheOwnersName_IsRefused(string password, string address, string name)
    {
        string? why = PasswordPolicy.Check(password, address, name);
        Assert.NotNull(why);
        Assert.Contains("name", why!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongPhraseNeedsNoSymbols_ButIsStillCheckedAgainstTheBlocklist()
    {
        Assert.Null(PasswordPolicy.Check("the ward round is at nine"));
        Assert.NotNull(PasswordPolicy.Check("apulki hospital password"));   // blocklisted words
    }

    [Fact]
    public void TheMinimumIsEight()
    {
        Assert.Equal(8, PasswordPolicy.MinimumLength);
        Assert.Null(PasswordPolicy.Check("Ward#Rou7"));       // exactly 9
        Assert.NotNull(PasswordPolicy.Check("Ward#Ro7"[..7]));
    }
}
