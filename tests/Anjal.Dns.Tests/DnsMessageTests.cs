namespace Anjal.Dns.Tests;

public class DnsMessageTests
{
    private static byte[] BuildSampleMxResponse()
    {
        // Hand-construct a response for "example.com" with two MX records:
        //   10 mx1.example.com
        //   20 mx2.example.com
        // using compression pointers.
        var ms = new System.IO.MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Label(string s) { ms.WriteByte((byte)s.Length); ms.Write(System.Text.Encoding.ASCII.GetBytes(s)); }
        void Ptr(int o) { ms.WriteByte((byte)(0xC0 | (o >> 8))); ms.WriteByte((byte)o); }

        U16(0x1234); U16(0x8180); U16(1); U16(2); U16(0); U16(0);
        int qnameStart = (int)ms.Position;
        Label("example"); Label("com"); ms.WriteByte(0);
        U16(15); U16(1);

        Ptr(qnameStart);
        U16(15); U16(1); U32(300); U16(8);
        U16(10); Label("mx1"); Ptr(qnameStart);

        Ptr(qnameStart);
        U16(15); U16(1); U32(300); U16(8);
        U16(20); Label("mx2"); Ptr(qnameStart);

        return ms.ToArray();
    }

    [Fact]
    public void Parse_ValidMxResponse_ReturnsRecords()
    {
        byte[] raw = BuildSampleMxResponse();
        DnsMessage msg = DnsMessage.Parse(raw);

        Assert.Equal(0x1234, msg.TransactionId);
        Assert.Equal(0, msg.ResponseCode);
        Assert.False(msg.Truncated);
        Assert.Equal(2, msg.Answers.Count);

        Assert.NotNull(msg.Answers[0].MxRecord);
        Assert.Equal(10, msg.Answers[0].MxRecord!.Priority);
        Assert.Equal("mx1.example.com", msg.Answers[0].MxRecord.Exchange);

        Assert.Equal(20, msg.Answers[1].MxRecord!.Priority);
        Assert.Equal("mx2.example.com", msg.Answers[1].MxRecord.Exchange);
    }

    [Fact]
    public void Parse_TooShort_Throws()
    {
        Assert.Throws<DnsException>(() => DnsMessage.Parse(new byte[5]));
    }

    [Fact]
    public void BuildQuery_ProducesExpectedHeaderAndQuestion()
    {
        byte[] q = DnsResolver.BuildQuery(0xABCD, "example.com", 15);

        // 12-byte header + qname(13) + qtype(2) + qclass(2) = 29.
        Assert.Equal(29, q.Length);

        // Transaction ID 0xABCD.
        Assert.Equal(0xAB, q[0]);
        Assert.Equal(0xCD, q[1]);
        // Flags 0x0100 (QR=0, RD=1).
        Assert.Equal(0x01, q[2]);
        Assert.Equal(0x00, q[3]);
        // QDCOUNT = 1.
        Assert.Equal(0x00, q[4]);
        Assert.Equal(0x01, q[5]);
        // ANCOUNT = 0.
        Assert.Equal(0x00, q[6]);
        Assert.Equal(0x00, q[7]);

        // QType MX = 15 at offset 25-26.
        Assert.Equal(0x00, q[25]);
        Assert.Equal(15, q[26]);
        // QClass IN = 1 at offset 27-28.
        Assert.Equal(0x00, q[27]);
        Assert.Equal(0x01, q[28]);
    }

    [Fact]
    public void BuildQuery_LowercasesDomain()
    {
        byte[] a = DnsResolver.BuildQuery(1, "EXAMPLE.com", 15);
        byte[] b = DnsResolver.BuildQuery(1, "example.com", 15);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildQuery_TooLongLabel_Throws()
    {
        string longLabel = new('a', 64);
        Assert.Throws<System.ArgumentException>(() => DnsResolver.BuildQuery(1, longLabel + ".com", 15));
    }
}
