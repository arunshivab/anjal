namespace Anjal.Mime.Tests;

public class HeaderCollectionTests
{
    private static readonly string[] ExpectedEnumerationOrder = new[] { "A", "B", "A" };

    [Fact]
    public void Add_ThenGet_ReturnsValue()
    {
        var hc = new HeaderCollection();
        hc.Add("Subject", "Hello");
        Assert.Equal("Hello", hc.Get("Subject"));
    }

    [Fact]
    public void Get_CaseInsensitive()
    {
        var hc = new HeaderCollection();
        hc.Add("subject", "Hello");
        Assert.Equal("Hello", hc.Get("SUBJECT"));
        Assert.Equal("Hello", hc.Get("Subject"));
    }

    [Fact]
    public void Get_Missing_ReturnsNull()
    {
        var hc = new HeaderCollection();
        Assert.Null(hc.Get("Subject"));
    }

    [Fact]
    public void Set_ReplacesExisting()
    {
        var hc = new HeaderCollection();
        hc.Add("Subject", "A");
        hc.Add("Subject", "B");
        Assert.Equal(2, hc.Count);
        hc.Set("Subject", "C");
        Assert.Equal(1, hc.Count);
        Assert.Equal("C", hc.Get("Subject"));
    }

    [Fact]
    public void Add_Duplicates_Preserved()
    {
        var hc = new HeaderCollection();
        hc.Add("Received", "from a");
        hc.Add("Received", "from b");
        var all = hc.GetAll("Received");
        Assert.Equal(2, all.Count);
        Assert.Equal("from a", all[0]);
        Assert.Equal("from b", all[1]);
    }

    [Fact]
    public void Remove_RemovesAllMatching()
    {
        var hc = new HeaderCollection();
        hc.Add("X-Tag", "1");
        hc.Add("Subject", "S");
        hc.Add("X-Tag", "2");
        int removed = hc.Remove("X-Tag");
        Assert.Equal(2, removed);
        Assert.Equal(1, hc.Count);
        Assert.Equal("S", hc.Get("Subject"));
    }

    [Fact]
    public void Contains_CaseInsensitive()
    {
        var hc = new HeaderCollection();
        hc.Add("From", "alice@x");
        Assert.True(hc.Contains("from"));
        Assert.True(hc.Contains("FROM"));
        Assert.False(hc.Contains("To"));
    }

    [Fact]
    public void Enumerator_PreservesInsertionOrder()
    {
        var hc = new HeaderCollection();
        hc.Add("A", "1");
        hc.Add("B", "2");
        hc.Add("A", "3");

        var names = new System.Collections.Generic.List<string>();
        foreach (var h in hc)
        {
            names.Add(h.Name);
        }
        Assert.Equal(ExpectedEnumerationOrder, names);
    }
}
