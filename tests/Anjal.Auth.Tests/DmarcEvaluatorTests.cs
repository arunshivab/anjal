namespace Anjal.Auth.Tests;

public class DmarcEvaluatorTests
{
    [Fact]
    public void ParseRecord_BasicReject()
    {
        var rec = DmarcEvaluator.ParseRecord("v=DMARC1; p=reject");
        Assert.Equal(DmarcPolicy.Reject, rec.Policy);
        Assert.Equal(AlignmentMode.Relaxed, rec.SpfAlignment);
        Assert.Equal(AlignmentMode.Relaxed, rec.DkimAlignment);
    }

    [Fact]
    public void ParseRecord_None()
    {
        var rec = DmarcEvaluator.ParseRecord("v=DMARC1; p=none; rua=mailto:dmarc@ex.test");
        Assert.Equal(DmarcPolicy.None, rec.Policy);
    }

    [Fact]
    public void ParseRecord_Quarantine()
    {
        var rec = DmarcEvaluator.ParseRecord("v=DMARC1; p=quarantine");
        Assert.Equal(DmarcPolicy.Quarantine, rec.Policy);
    }

    [Fact]
    public void ParseRecord_StrictAlignment()
    {
        var rec = DmarcEvaluator.ParseRecord("v=DMARC1; p=reject; aspf=s; adkim=s");
        Assert.Equal(AlignmentMode.Strict, rec.SpfAlignment);
        Assert.Equal(AlignmentMode.Strict, rec.DkimAlignment);
    }

    [Fact]
    public void ParseRecord_MixedAlignment()
    {
        var rec = DmarcEvaluator.ParseRecord("v=DMARC1; p=reject; aspf=s; adkim=r");
        Assert.Equal(AlignmentMode.Strict, rec.SpfAlignment);
        Assert.Equal(AlignmentMode.Relaxed, rec.DkimAlignment);
    }

    [Fact]
    public void ParseRecord_MissingV_Throws()
    {
        System.Exception? caught = null;
        try { DmarcEvaluator.ParseRecord("p=reject"); }
#pragma warning disable CA1031
        catch (System.Exception ex) { caught = ex; }
#pragma warning restore CA1031
        Assert.NotNull(caught);
    }

    [Fact]
    public void ParseRecord_MissingP_Throws()
    {
        System.Exception? caught = null;
        try { DmarcEvaluator.ParseRecord("v=DMARC1"); }
#pragma warning disable CA1031
        catch (System.Exception ex) { caught = ex; }
#pragma warning restore CA1031
        Assert.NotNull(caught);
    }

    [Fact]
    public void ParseRecord_UnknownP_Throws()
    {
        System.Exception? caught = null;
        try { DmarcEvaluator.ParseRecord("v=DMARC1; p=enforce"); }
#pragma warning disable CA1031
        catch (System.Exception ex) { caught = ex; }
#pragma warning restore CA1031
        Assert.NotNull(caught);
    }
}
