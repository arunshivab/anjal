using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>rc.29 (DES-22 §3.1, §4.2, §7.4): a page as shown and as held, and the frames that cross the socket.</summary>
public sealed class GeometryAndFramesTests
{
    [Theory]
    [InlineData(0, 10, 10, 10, 190)]
    [InlineData(90, 10, 10, 10, 10)]
    [InlineData(180, 10, 10, 90, 10)]
    [InlineData(270, 10, 10, 190, 90)]
    public void ThePageAsShown_UndoesItsTurn(int rotate, double ux, double uy, double sx, double sy)
    {
        // A 100 x 200 page; the point (10, 10) is near its bottom-left corner.
        var g = new PageGeometry(0, 0, 100, 200, rotate);
        (double x, double y) = g.ToShown(ux, uy);
        Assert.Equal(sx, x, 6);
        Assert.Equal(sy, y, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void EveryCorner_GoesThereAndBack_WithACropBox(int rotate)
    {
        // A crop box that does not start at the origin (its lower-left at 30, 40).
        var g = new PageGeometry(30, 40, 300, 500, rotate);
        foreach ((double x, double y) in new[] { (30.0, 40.0), (330.0, 40.0), (30.0, 540.0), (330.0, 540.0), (100.0, 300.0) })
        {
            (double sx, double sy) = g.ToShown(x, y);
            Assert.InRange(sx, -0.001, g.ShownWidth + 0.001);
            Assert.InRange(sy, -0.001, g.ShownHeight + 0.001);
            (double bx, double by) = g.ToUser(sx, sy);
            Assert.Equal(x, bx, 6);
            Assert.Equal(y, by, 6);
        }
    }

    [Fact]
    public void ATurnedPage_IsShownWithItsSidesSwapped()
    {
        var g = new PageGeometry(0, 0, 842, 595, 90);
        Assert.Equal(595, g.ShownWidth);
        Assert.Equal(842, g.ShownHeight);
        Assert.Equal(90, g.ToShownAngle(0));
        Assert.Equal(0, new PageGeometry(0, 0, 10, 10, 0).ToShownAngle(0));
    }

    [Fact]
    public async Task AFrame_GoesThereAndBack()
    {
        using var ms = new MemoryStream();
        await ViewFrames.WriteAsync(ms, "{\"a\":1}", new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3 }, Array.Empty<byte>() });
        ms.Position = 0;
        (string json, List<byte[]> files) = (await ViewFrames.ReadAsync(ms))!.Value;
        Assert.Equal("{\"a\":1}", json);
        Assert.Equal(new byte[] { 1, 2, 3 }, files[0]);
        Assert.Empty(files[1]);
        Assert.Null(await ViewFrames.ReadAsync(ms));
    }

    [Fact]
    public async Task AFrameClaimingTooMuch_IsRefused_BeforeItIsRead()
    {
        byte[] head = "AVW1"u8.ToArray().Concat(BitConverter.GetBytes(int.MaxValue)).Concat(BitConverter.GetBytes(1)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewFrames.ReadAsync(new MemoryStream(head)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewFrames.ReadAsync(new MemoryStream("GET / HTTP/1.1\r\n"u8.ToArray())));
    }

    [Fact]
    public void FileKinds_AreJudgedByNameAndBytes()
    {
        Assert.Equal(FileKinds.Word, FileKinds.Of("a.DOCX", null));
        Assert.Equal(FileKinds.Sheet, FileKinds.Of("x", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.Null(FileKinds.Of("a.svg", "image/svg+xml"));
        byte[] dicom = new byte[200];
        "DICM"u8.CopyTo(dicom.AsSpan(128));
        Assert.True(FileKinds.IsDicom(dicom));
        Assert.Equal("image/png", FileKinds.PictureType(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
        Assert.Null(FileKinds.PictureType("<svg"u8));
    }
}
