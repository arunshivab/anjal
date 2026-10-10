namespace Anjal.Smtp.Tests;

/// <summary>rc.13: the password rules, each judged on its own for the list beside a new password.</summary>
public sealed class Rc13PasswordRulesTests
{
    [Theory]
    [InlineData("Krishna@123")]
    [InlineData("Sunshine#2024")]
    [InlineData("sairam1234")]
    [InlineData("Abcd@1234")]
    [InlineData("Krishna@123456789")]
    public void KnownLeakedPasswords_AreRefused_HoweverDressedUp(string password)
    {
        Assert.False(PasswordPolicy.Evaluate(password).NotCommon);
        Assert.NotNull(PasswordPolicy.Check(password));
    }

    [Fact]
    public void EachRule_IsJudgedOnItsOwn()
    {
        PasswordRuleResults r = PasswordPolicy.Evaluate("meera", "meera.iyer@anjal.co.in", "Meera Iyer");
        Assert.False(r.LongEnough);
        Assert.False(r.NotPersonal);
        Assert.False(r.All);

        r = PasswordPolicy.Evaluate("Quiet harbour mornings", "meera.iyer@anjal.co.in", "Meera Iyer");
        Assert.True(r.All);
        Assert.Null(PasswordPolicy.Check("Quiet harbour mornings", "meera.iyer@anjal.co.in", "Meera Iyer"));

        Assert.False(PasswordPolicy.Evaluate(string.Empty).NotCommon);
        Assert.False(PasswordPolicy.Evaluate("Teal-Lantern-11", minimumLength: 16).LongEnough);
        Assert.Contains("16", PasswordPolicy.Check("Teal-Lantern-11", minimumLength: 16), StringComparison.Ordinal);
        Assert.Null(PasswordPolicy.Check("Teal-Lantern-11"));
        Assert.False(PasswordPolicy.Evaluate("Teal-Lantern-1").LongEnough);   // 14: one short of 15 alone
    }
}
