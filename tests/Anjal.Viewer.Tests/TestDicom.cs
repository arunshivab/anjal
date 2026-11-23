using System.Globalization;
using System.Text;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.30: small made-up DICOM Part 10 files for the tests (no real patient's data): Explicit VR Little Endian, 16-bit
/// MONOCHROME2 pixels in a gradient, written element by element in tag order.
/// </summary>
internal static class TestDicom
{
    public const string StudyUid = "1.2.826.0.1.3680043.10.999.1";

    /// <summary>One slice.</summary>
    public static byte[] Slice(string series, int number, double z, string modality = "CT", string description = "Chest", int size = 16, int frames = 1, string patient = "TEST^PATIENT", string patientId = "TEST-0001")
    {
        string sop = "1.2.826.0.1.3680043.10.999.3." + series + "." + number.ToString(CultureInfo.InvariantCulture);
        var data = new List<(ushort Group, ushort Element, string Vr, byte[] Value)>
        {
            (0x0008, 0x0016, "UI", Uid("1.2.840.10008.5.1.4.1.1.2")),
            (0x0008, 0x0018, "UI", Uid(sop)),
            (0x0008, 0x0020, "DA", Text("20261118")),
            (0x0008, 0x0060, "CS", Text(modality)),
            (0x0008, 0x103E, "LO", Text(description)),
            (0x0010, 0x0010, "PN", Text(patient)),
            (0x0010, 0x0020, "LO", Text(patientId)),
            (0x0020, 0x000D, "UI", Uid(StudyUid)),
            (0x0020, 0x000E, "UI", Uid("1.2.826.0.1.3680043.10.999.2." + series)),
            (0x0020, 0x0011, "IS", Text(series)),
            (0x0020, 0x0013, "IS", Text(number.ToString(CultureInfo.InvariantCulture))),
            (0x0020, 0x0032, "DS", Text("0\\0\\" + z.ToString("0.0", CultureInfo.InvariantCulture))),
            (0x0020, 0x0037, "DS", Text("1\\0\\0\\0\\1\\0")),
            (0x0028, 0x0002, "US", U16(1)),
            (0x0028, 0x0004, "CS", Text("MONOCHROME2")),
        };
        if (frames > 1)
        {
            data.Add((0x0028, 0x0008, "IS", Text(frames.ToString(CultureInfo.InvariantCulture))));
        }
        data.Add((0x0028, 0x0010, "US", U16(size)));
        data.Add((0x0028, 0x0011, "US", U16(size)));
        data.Add((0x0028, 0x0100, "US", U16(16)));
        data.Add((0x0028, 0x0101, "US", U16(12)));
        data.Add((0x0028, 0x0102, "US", U16(11)));
        data.Add((0x0028, 0x0103, "US", U16(0)));
        byte[] pixels = new byte[size * size * 2 * frames];
        for (int i = 0; i < size * size * frames; i++)
        {
            ushort v = (ushort)((i % (size * size)) * 4095 / (size * size));
            pixels[2 * i] = (byte)v;
            pixels[(2 * i) + 1] = (byte)(v >> 8);
        }
        data.Add((0x7FE0, 0x0010, "OW", pixels));
        return Part10(sop, data);
    }

    /// <summary>A file with the DICM mark whose pixel element claims far more than it holds.</summary>
    public static byte[] Truncated()
    {
        byte[] whole = Slice("9", 1, 0);
        return whole[..(whole.Length - 300)];
    }

    /// <summary>A file with the DICM mark and a sequence nested far past any limit.</summary>
    public static byte[] DeeplyNested(int depth = 5000)
    {
        using var body = new MemoryStream();
        for (int i = 0; i < depth; i++)
        {
            // (0040,A730) Content Sequence, undefined length; an item of undefined length.
            Write(body, 0x0040, 0xA730);
            body.Write("SQ"u8);
            body.Write(new byte[2]);
            body.Write(BitConverter.GetBytes(0xFFFFFFFFu));
            Write(body, 0xFFFE, 0xE000);
            body.Write(BitConverter.GetBytes(0xFFFFFFFFu));
        }
        return Part10("1.2.826.0.1.3680043.10.999.9", new List<(ushort, ushort, string, byte[])>(), body.ToArray());
    }

    private static byte[] Part10(string sop, List<(ushort Group, ushort Element, string Vr, byte[] Value)> data, byte[]? tail = null)
    {
        using var meta = new MemoryStream();
        Element(meta, 0x0002, 0x0001, "OB", new byte[] { 0, 1 });
        Element(meta, 0x0002, 0x0002, "UI", Uid("1.2.840.10008.5.1.4.1.1.2"));
        Element(meta, 0x0002, 0x0003, "UI", Uid(sop));
        Element(meta, 0x0002, 0x0010, "UI", Uid("1.2.840.10008.1.2.1"));
        using var file = new MemoryStream();
        file.Write(new byte[128]);
        file.Write("DICM"u8);
        Element(file, 0x0002, 0x0000, "UL", BitConverter.GetBytes((uint)meta.Length));
        meta.WriteTo(file);
        foreach ((ushort g, ushort e, string vr, byte[] v) in data)
        {
            Element(file, g, e, vr, v);
        }
        if (tail is not null)
        {
            file.Write(tail);
        }
        return file.ToArray();
    }

    private static void Element(Stream s, ushort group, ushort element, string vr, byte[] value)
    {
        Write(s, group, element);
        s.Write(Encoding.ASCII.GetBytes(vr));
        if (vr is "OB" or "OW" or "SQ" or "UN" or "UT")
        {
            s.Write(new byte[2]);
            s.Write(BitConverter.GetBytes((uint)value.Length));
        }
        else
        {
            s.Write(BitConverter.GetBytes((ushort)value.Length));
        }
        s.Write(value);
    }

    private static void Write(Stream s, ushort group, ushort element)
    {
        s.Write(BitConverter.GetBytes(group));
        s.Write(BitConverter.GetBytes(element));
    }

    // A UID is padded with a NUL to an even length; text with a space.
    private static byte[] Uid(string uid) => Encoding.ASCII.GetBytes(uid.Length % 2 == 0 ? uid : uid + "\0");

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text.Length % 2 == 0 ? text : text + " ");

    private static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);
}
