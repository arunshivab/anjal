using System.Security.Cryptography;
using System.Text;

namespace Anjal.Store.Tests;

/// <summary>rc.25 (DES-15): the envelope, the keys, the shares and the paper form.</summary>
public sealed class MailCryptoTests
{
    private static readonly Lazy<(byte[] Public, byte[] Private)> Pair = new(MailKeys.NewKeyPair);
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes("Subject: Biopsy report - Ramesh K, MRN 1042\r\n\r\nResult attached.");

    [Fact]
    public void Envelope_ToPublicKey_OpensOnlyWithThePrivateKey()
    {
        byte[] env = MailEnvelope.SealToPublicKey(Sample, Pair.Value.Public);
        Assert.True(MailEnvelope.IsEnvelope(env));
        Assert.Equal(-1, Encoding.UTF8.GetString(env).IndexOf("Ramesh", StringComparison.Ordinal));
        using var empty = new MailKeyring();
        Assert.Null(MailEnvelope.Open(env, empty));
        using var ring = new MailKeyring();
        ring.Add(Guid.NewGuid(), Pair.Value.Private, MailKeys.NewSymmetricKey());
        Assert.Equal(Sample, MailEnvelope.Open(env, ring));
    }

    [Fact]
    public void Envelope_Rekeyed_OpensWithTheDataKeyAlone_AndChangedBytesAreRefused()
    {
        byte[] dataKey = MailKeys.NewSymmetricKey();
        byte[] env = MailEnvelope.SealToPublicKey(Sample, Pair.Value.Public);
        using var ring = new MailKeyring();
        ring.Add(Guid.NewGuid(), Pair.Value.Private, dataKey);
        byte[] rekeyed = MailEnvelope.Rekey(env, ring, dataKey)!;
        Assert.Equal(MailEnvelope.KindSymmetric, MailEnvelope.Describe(rekeyed)!.Value.Kind);
        Assert.Equal(Sample, MailKeys.Unwrap(rekeyed, dataKey));
        rekeyed[^1] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => MailEnvelope.Open(rekeyed, ring));
    }

    [Fact]
    public void Fields_RoundTrip_PlainTextPassesThrough_AndLockedGivesNull()
    {
        string field = MailEnvelope.FieldToPublicKey("HIV test result - bed 12", Pair.Value.Public);
        Assert.StartsWith(MailEnvelope.FieldPrefix, field, StringComparison.Ordinal);
        Assert.DoesNotContain("HIV", field, StringComparison.Ordinal);
        using var empty = new MailKeyring();
        Assert.Null(MailEnvelope.OpenField(field, empty));
        Assert.Equal("never encrypted", MailEnvelope.OpenField("never encrypted", empty));
        byte[] dataKey = MailKeys.NewSymmetricKey();
        using var ring = new MailKeyring();
        ring.Add(Guid.NewGuid(), Pair.Value.Private, dataKey);
        Assert.Equal("HIV test result - bed 12", MailEnvelope.OpenField(field, ring));
        string rekeyed = MailEnvelope.RekeyField(field, ring, dataKey);
        Assert.NotEqual(field, rekeyed);
        Assert.True(rekeyed.Length < field.Length);
        Assert.Equal("HIV test result - bed 12", MailEnvelope.OpenField(rekeyed, ring));
    }

    [Fact]
    public void UnlockKey_DependsOnPasswordSaltAndPepper()
    {
        byte[] salt = MailKeys.NewSalt();
        byte[] pepper = RandomNumberGenerator.GetBytes(32);
        byte[] k = MailKeys.UnlockKey("correct horse", salt, pepper);
        Assert.Equal(k, MailKeys.UnlockKey("correct horse", salt, pepper));
        Assert.NotEqual(k, MailKeys.UnlockKey("correct horse", salt, RandomNumberGenerator.GetBytes(32)));
        Assert.NotEqual(k, MailKeys.UnlockKey("Correct horse", salt, pepper));
        byte[] wrapped = MailKeys.Wrap(Pair.Value.Private, k);
        Assert.Equal(Pair.Value.Private, MailKeys.Unwrap(wrapped, k));
        Assert.Null(MailKeys.Unwrap(wrapped, MailKeys.UnlockKey("wrong", salt, pepper)));
    }

    [Fact]
    public void Shares_AnyTwoRebuild_OneRevealsNothingUseful()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        IReadOnlyList<byte[]> shares = SecretShares.Split(secret);
        Assert.Equal(3, shares.Count);
        Assert.Equal(secret, SecretShares.Combine(shares[0], shares[1]));
        Assert.Equal(secret, SecretShares.Combine(shares[2], shares[0]));
        Assert.Equal(secret, SecretShares.Combine(shares[1], shares[2]));
        Assert.Throws<ArgumentException>(() => SecretShares.Combine(shares[0], shares[0]));
        Assert.NotEqual(secret, shares[0].AsSpan(1).ToArray());
    }

    [Fact]
    public void Shares_GaloisField_KnownValues()
    {
        // The AES field: 0x57 x 0x83 = 0xC1 (FIPS-197 §4.2), and x 1 is itself.
        Assert.Equal(0xC1, SecretShares.Multiply(0x57, 0x83));
        Assert.Equal(0x57, SecretShares.Multiply(0x57, 1));
        Assert.Equal(0, SecretShares.Multiply(0, 0x83));
    }

    [Fact]
    public void PaperForm_RoundTrips_AndCatchesATypingMistake()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string paper = PaperForm.Write(secret);
        Assert.Equal(17, paper.Split(' ').Length);
        Assert.Equal(secret, PaperForm.Read(paper.ToLowerInvariant()));
        char[] typo = paper.ToCharArray();
        typo[0] = typo[0] == 'A' ? 'B' : 'A';
        Assert.Null(PaperForm.Read(new string(typo)));
    }
}
