namespace Anjal.Mime.Tests;

public class ModuleInfoTests
{
    [Fact]
    public void Version_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ModuleInfo.Version));
    }

    [Fact]
    public void Name_IsExpected()
    {
        Assert.Equal("Anjal.Mime", ModuleInfo.Name);
    }
}
