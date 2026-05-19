namespace Anjal.Mime.Tests;

public class Base64CodecTests
{
    [Fact]
    public void Encode_EmptyArray_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Base64Codec.Encode(System.Array.Empty<byte>()));
    }

    [Fact]
    public void Encode_KnownVectors_ProducesRfc4648Output()
    {
        // RFC 4648 section 10 test vectors.
        Assert.Equal("Zg==", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("f")));
        Assert.Equal("Zm8=", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("fo")));
        Assert.Equal("Zm9v", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("foo")));
        Assert.Equal("Zm9vYg==", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("foob")));
        Assert.Equal("Zm9vYmE=", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("fooba")));
        Assert.Equal("Zm9vYmFy", Base64Codec.Encode(System.Text.Encoding.ASCII.GetBytes("foobar")));
    }

    [Fact]
    public void Decode_KnownVectors_RoundTrips()
    {
        Assert.Equal("f", System.Text.Encoding.ASCII.GetString(Base64Codec.Decode("Zg==")));
        Assert.Equal("foo", System.Text.Encoding.ASCII.GetString(Base64Codec.Decode("Zm9v")));
        Assert.Equal("foobar", System.Text.Encoding.ASCII.GetString(Base64Codec.Decode("Zm9vYmFy")));
    }

    [Fact]
    public void Decode_SkipsWhitespaceAndLineBreaks()
    {
        string padded = "Zm9v\r\nYmFy";
        byte[] decoded = Base64Codec.Decode(padded);
        Assert.Equal("foobar", System.Text.Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Encode_LongInput_WrapsAt76Columns()
    {
        // 60 bytes -> 80 chars base64, should split into 76 + CRLF + 4.
        byte[] data = new byte[60];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        string encoded = Base64Codec.Encode(data);
        Assert.Contains("\r\n", encoded, System.StringComparison.Ordinal);
        // First line must be exactly 76 chars before the CRLF.
        int firstBreak = encoded.IndexOf("\r\n", System.StringComparison.Ordinal);
        Assert.Equal(76, firstBreak);
    }

    [Fact]
    public void Decode_InvalidCharacter_Throws()
    {
        Assert.Throws<System.FormatException>(() => Base64Codec.Decode("Zg=*="));
    }

    [Fact]
    public void RoundTrip_RandomBinary_Preserved()
    {
        byte[] data = new byte[257];
        var rng = new System.Random(42);
        rng.NextBytes(data);

        string encoded = Base64Codec.Encode(data);
        byte[] decoded = Base64Codec.Decode(encoded);

        Assert.Equal(data, decoded);
    }
}
