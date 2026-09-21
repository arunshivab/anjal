using System.Xml.Linq;

namespace Anjal.Api.Tests;

/// <summary>
/// DEF-004: every module used to report a hard-coded "0.1.0", whatever was
/// built. Versions now come from the compiled assembly, so they all agree
/// with Directory.Build.props - the single place a release number is set.
/// </summary>
public class VersionTests
{
    private static string SourceVersion()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        XDocument props = XDocument.Load(Path.Combine(dir!.FullName, "Directory.Build.props"));
        return props.Descendants("Version").First().Value.Trim();
    }

    [Fact]
    public void EveryModule_ReportsTheVersionInDirectoryBuildProps()
    {
        string expected = SourceVersion();
        string[] reported =
        {
            Anjal.Api.ModuleInfo.Version,
            Anjal.Smtp.ModuleInfo.Version,
            Anjal.Mime.ModuleInfo.Version,
            Anjal.Store.ModuleInfo.Version,
            Anjal.Routing.ModuleInfo.Version,
            Anjal.Mailbox.ModuleInfo.Version,
            Anjal.Spam.ModuleInfo.Version,
            Anjal.Acme.ModuleInfo.Version,
            Anjal.Dns.ModuleInfo.Version,
        };
        Assert.All(reported, v => Assert.Equal(expected, v));
        Assert.NotEqual("0.1.0", expected);
    }
}
