namespace Anjal.Mime.Tests;

public class QuotedPrintableCodecTests
{
    [Fact]
    public void Encode_PlainAscii_PassesThrough()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("Hello, World!");
        Assert.Equal("Hello, World!", QuotedPrintableCodec.Encode(data));
    }

    [Fact]
    public void Encode_NonAsciiByte_HexEscaped()
    {
        byte[] data = new byte[] { (byte)'a', 0xE9, (byte)'b' };
        Assert.Equal("a=E9b", QuotedPrintableCodec.Encode(data));
    }

    [Fact]
    public void Encode_EqualsSign_Escaped()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("1+1=2");
        Assert.Equal("1+1=3D2", QuotedPrintableCodec.Encode(data));
    }

    [Fact]
    public void Decode_HexEscape_BackToBinary()
    {
        byte[] result = QuotedPrintableCodec.Decode("a=E9b");
        Assert.Equal(new byte[] { (byte)'a', 0xE9, (byte)'b' }, result);
    }

    [Fact]
    public void Decode_SoftLineBreak_Removed()
    {
        // "abc=" + CRLF + "def" should decode to "abcdef".
        byte[] result = QuotedPrintableCodec.Decode("abc=\r\ndef");
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes("abcdef"), result);
    }

    [Fact]
    public void Decode_BareLfSoftBreak_AlsoRemoved()
    {
        byte[] result = QuotedPrintableCodec.Decode("abc=\ndef");
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes("abcdef"), result);
    }

    [Fact]
    public void Decode_LowercaseHex_Accepted()
    {
        byte[] result = QuotedPrintableCodec.Decode("=e9");
        Assert.Single(result);
        Assert.Equal((byte)0xE9, result[0]);
    }

    [Fact]
    public void RoundTrip_MixedContent_Preserved()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("Caf\u00e9 and a equals sign = here.");
        string encoded = QuotedPrintableCodec.Encode(data);
        byte[] decoded = QuotedPrintableCodec.Decode(encoded);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Encode_LongLine_WrapsBeforeColumn76()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes(new string('a', 200));
        string encoded = QuotedPrintableCodec.Encode(data);

        foreach (string line in encoded.Split("\r\n", System.StringSplitOptions.None))
        {
            Assert.True(line.Length <= 76, $"Line exceeded 76 chars: '{line}'");
        }
    }
}
