namespace Anjal.Smtp.Tests;

public class Pbkdf2HasherTests
{
    [Fact]
    public void Hash_ProducesPbkdf2Format()
    {
        string h = Pbkdf2Hasher.Hash("hello", iterations: 1000);
        Assert.StartsWith("pbkdf2$1000$", h, System.StringComparison.Ordinal);
        // Format: pbkdf2 $ iterations $ salt-b64 $ hash-b64
        string[] parts = h.Split('$');
        Assert.Equal(4, parts.Length);
    }

    [Fact]
    public void Hash_DifferentSalts_ProducesDifferentHashes()
    {
        // Same password hashed twice -> different output because salt is random.
        string a = Pbkdf2Hasher.Hash("samepassword", iterations: 1000);
        string b = Pbkdf2Hasher.Hash("samepassword", iterations: 1000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        string h = Pbkdf2Hasher.Hash("correct horse battery staple", iterations: 1000);
        Assert.True(Pbkdf2Hasher.Verify("correct horse battery staple", h));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        string h = Pbkdf2Hasher.Hash("correct password", iterations: 1000);
        Assert.False(Pbkdf2Hasher.Verify("wrong password", h));
    }

    [Fact]
    public void Verify_MalformedHash_NoPbkdfPrefix_ReturnsFalse()
    {
        Assert.False(Pbkdf2Hasher.Verify("any", "not-a-valid-hash-format"));
    }

    [Fact]
    public void Verify_MalformedHash_WrongFieldCount_ReturnsFalse()
    {
        Assert.False(Pbkdf2Hasher.Verify("any", "pbkdf2$1000$onlytwoparts"));
    }

    [Fact]
    public void Verify_MalformedHash_BadIterations_ReturnsFalse()
    {
        Assert.False(Pbkdf2Hasher.Verify("any", "pbkdf2$notanumber$YWFh$YmJi"));
    }

    [Fact]
    public void Verify_MalformedHash_BadBase64_ReturnsFalse()
    {
        Assert.False(Pbkdf2Hasher.Verify("any", "pbkdf2$1000$!!!notbase64!!!$YmJi"));
    }

    [Fact]
    public void Verify_EmptyPassword_AgainstHashOfEmpty_ReturnsTrue()
    {
        string h = Pbkdf2Hasher.Hash(string.Empty, iterations: 1000);
        Assert.True(Pbkdf2Hasher.Verify(string.Empty, h));
    }

    [Fact]
    public void Hash_ZeroIterations_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Pbkdf2Hasher.Hash("x", iterations: 0));
    }
}
