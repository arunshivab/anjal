using System.Text;
using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.29 part 2 (decisions of 15 Nov 2026, Chuvadi.Zip 1.0.0): archives made by other programs. A ZipCrypto archive
/// (the old ZIP password, made with Info-ZIP's <c>zip -P lotus</c>), one wrong password that passes ZipCrypto's
/// one-byte check by chance (1 in 256), and entries packed with BZip2 and LZMA (made with Python's zipfile).
/// </summary>
public sealed class ZipMethodTests
{
    // zip -X -P lotus zipcrypto.zip notes.txt (20 lines of "Private notes, locked with ZipCrypto.").
    private const string ZipCrypto = "UEsDBBQACQAIAABIcF15hcF6PAAAAPgCAAAJAAAAbm90ZXMudHh0uxI+r/gJoNflVx0lXBa0jN490GHn6ls+U1x0nHAw4/XIKtN0jlId8sj3yQ8zvIlF7mILHHXvTmMVkV9gUEsHCHmFwXo8AAAA+AIAAFBLAQIeAxQACQAIAABIcF15hcF6PAAAAPgCAAAJAAAAAAAAAAEAAACkgQAAAABub3Rlcy50eHRQSwUGAAAAAAEAAQA3AAAAcwAAAAAA";

    // Python zipfile: bzip2.txt (BZip2), lzma.txt (LZMA) and ok.txt (Deflate).
    private const string Methods = "UEsDBC4AAAAMAABIcF2Nj+SbTwAAAPgCAAAJAAAAYnppcDIudHh0QlpoOTFBWSZTWRX0xOMAAIufgEABEAAQAEAQLmhEgCAAcFMABNApVAAByJiJwJmJ4JoE1E2E4E6E8E9E7E+E9E+Ccifi7kinChICvpicYFBLAwQ/AAIADgAASHBd46xSKC0AAADQAgAACAAAAGx6bWEudHh0CQQFAF0AAIAAACgYSGbcCPYPRyyyT4jgchni89JtvzlNdiZ9YjK3//86oAAAUEsDBBQAAAAIAABIcF2SVKm+BgAAAAQAAAAGAAAAb2sudHh0S8vMSwUAUEsBAi4DLgAAAAwAAEhwXY2P5JtPAAAA+AIAAAkAAAAAAAAAAAAAAIABAAAAAGJ6aXAyLnR4dFBLAQI/Az8AAgAOAABIcF3jrFIoLQAAANACAAAIAAAAAAAAAAAAAACAAXYAAABsem1hLnR4dFBLAQIUAxQAAAAIAABIcF2SVKm+BgAAAAQAAAAGAAAAAAAAAAAAAACAAckAAABvay50eHRQSwUGAAAAAAMAAwChAAAA8wAAAAAA";

    // "tulip126" passes ZipCrypto's check byte for this archive though it is not the password.
    private const string FalsePass = "tulip126";

    [Fact]
    public void AZipCryptoArchive_AsksForItsPassword_ThenShowsItsFile_ButGivesNoneOut()
    {
        byte[] zip = Convert.FromBase64String(ZipCrypto);
        Assert.Equal(ViewStatus.NeedsPassword, ArchiveReader.List(zip).Status);
        Assert.Equal(ViewStatus.WrongPassword, ArchiveReader.List(zip, null, new ArchivePasswords("tulip", null)).Status);
        ArchiveListing open = ArchiveReader.List(zip, null, new ArchivePasswords("lotus", null));
        Assert.True(open.Listed);
        Assert.True(open.View.Locked);
        EntryResult notes = ArchiveReader.Entry(zip, "0", new ArchivePasswords("lotus", null));
        Assert.StartsWith("Private notes, locked with ZipCrypto.", Encoding.UTF8.GetString(notes.Bytes!), StringComparison.Ordinal);
        ViewAnswer download = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = "0", ArchivePassword = "lotus" }, zip, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.Refused, download.Status);
    }

    [Fact]
    public void AWrongPasswordThatPassesZipCryptosCheck_IsStillAWrongPassword_NeverDamagedOrGarbage()
    {
        byte[] zip = Convert.FromBase64String(ZipCrypto);
        ArchiveListing listed = ArchiveReader.List(zip, null, new ArchivePasswords(FalsePass, null));
        EntryResult read = ArchiveReader.Entry(zip, "0", new ArchivePasswords(FalsePass, null));
        Assert.Equal(ViewStatus.WrongPassword, listed.Status);
        Assert.Equal(ViewStatus.WrongPassword, read.Status);
        Assert.Null(read.Bytes);
        Assert.Equal(ViewStatus.WrongPassword, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveList, Password = FalsePass }, zip, CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void EntriesPackedWithBZip2OrLzma_AreSaidPlainly_AndTheRestOpen()
    {
        byte[] zip = Convert.FromBase64String(Methods);
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Null(view.Refused);
        foreach (string name in new[] { "bzip2.txt", "lzma.txt" })
        {
            ArchiveItem item = view.Entries.Single(e => e.Name == name);
            Assert.True(item.Unsupported, name);
            ViewAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) }, zip, CancellationToken.None).Answer;
            Assert.Equal(ViewStatus.Unsupported, a.Status);
            Assert.Equal("zip-method", a.Detail);
        }
        ArchiveItem ok = view.Entries.Single(e => e.Name == "ok.txt");
        Assert.False(ok.Unsupported);
        Assert.Equal("fine", Encoding.UTF8.GetString(ArchiveReader.Entry(zip, ok.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Bytes!));
    }
}
