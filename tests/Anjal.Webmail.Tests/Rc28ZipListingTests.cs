using System.IO.Compression;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.28 (DES-18 §10, D-337): the archive list in the viewer - folders and sizes, programs blocked by
/// name and by their first bytes, a locked archive said to be locked, and an archive bomb refused at each
/// limit before anything is unpacked.
/// </summary>
public sealed class Rc28ZipListingTests
{
    private static readonly byte[] LinuxProgram = { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

    [Fact]
    public void AnOrdinaryArchive_IsListed_WithFoldersAndSizes()
    {
        byte[] zip = Zip(("reports/", null), ("reports/cbc.pdf", "%PDF-1.4 results"u8.ToArray()), ("notes.txt", "Call back on Tuesday."u8.ToArray()));
        ZipView view = ZipListing.Read(zip);
        Assert.Null(view.Refused);
        Assert.False(view.Locked);
        Assert.Equal(3, view.Entries.Count);
        ZipEntryView folder = Assert.Single(view.Entries, e => e.Folder);
        Assert.Equal("reports", folder.Name);
        ZipEntryView cbc = Assert.Single(view.Entries, e => e.Name == "cbc.pdf");
        Assert.Equal(1, cbc.Depth);
        Assert.Equal(16, cbc.Size);
        Assert.False(cbc.Blocked);
        (string name, byte[] bytes) = ZipListing.Entry(zip, cbc.Index)!.Value;
        Assert.Equal("cbc.pdf", name);
        Assert.Equal("%PDF-1.4 results"u8.ToArray(), bytes);
        Assert.Null(ZipListing.Entry(zip, folder.Index));
    }

    [Fact]
    public void Programs_AreBlocked_ByName_AndByWhatTheyAre_AndNeverGivenOut()
    {
        byte[] zip = Zip(("setup.exe", "anything"u8.ToArray()), ("readme.txt", LinuxProgram), ("photo.jpg", "\xFF\xD8\xFF\xE0"u8.ToArray()));
        ZipView view = ZipListing.Read(zip);
        Assert.True(Assert.Single(view.Entries, e => e.Name == "setup.exe").Blocked);
        ZipEntryView disguised = Assert.Single(view.Entries, e => e.Name == "readme.txt");
        Assert.True(disguised.Blocked);
        Assert.False(Assert.Single(view.Entries, e => e.Name == "photo.jpg").Blocked);
        Assert.Null(ZipListing.Entry(zip, disguised.Index));
    }

    [Fact]
    public void AnArchiveBomb_IsRefused_AtEachLimit_AndNothingIsGivenOut()
    {
        byte[] packed = Zip(("zeros.bin", new byte[20 * 1024 * 1024]));
        Assert.Equal(ZipView.TooPacked, ZipListing.Read(packed).Refused);
        Assert.Null(ZipListing.Entry(packed, 0));

        (string, byte[]?)[] many = Enumerable.Range(0, ZipListing.MaxEntries + 1).Select(i => ($"f{i}.txt", (byte[]?)new byte[] { 1 })).ToArray();
        Assert.Equal(ZipView.TooMany, ZipListing.Read(Zip(many)).Refused);

        Assert.Equal(ZipView.Unreadable, ZipListing.Read("not an archive"u8.ToArray()).Refused);
    }

    [Fact]
    public void ALockedArchive_IsSaidToBeLocked_AndGivesNothingOut()
    {
        byte[] zip = Zip(("results.pdf", "%PDF-1.4"u8.ToArray()));
        // Set the "encrypted" flag (bit 0) on the entry's local and central headers, as a locked archive has.
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04)
            {
                zip[i + 6] |= 1;
            }
            else if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                zip[i + 8] |= 1;
            }
        }
        ZipView view = ZipListing.Read(zip);
        Assert.True(view.Locked);
        Assert.Null(ZipListing.Entry(zip, 0));
    }

    private static byte[] Zip(params (string Name, byte[]? Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[]? bytes) in files)
            {
                ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (bytes is not null)
                {
                    using Stream s = e.Open();
                    s.Write(bytes);
                }
            }
        }
        return stream.ToArray();
    }
}
