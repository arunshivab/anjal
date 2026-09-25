using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// DEF-050: the keys that sign webmail sessions were stored wherever ASP.NET
/// chose, undocumented. ANJAL_WEBMAIL_KEYS_DIR now names the folder, and a
/// session protected before a restart must still be readable after it.
/// </summary>
public sealed class DataProtectionKeysTests : System.IDisposable
{
    private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-keys-" + System.Guid.NewGuid().ToString("N"));

    [Fact]
    public void KeysDirectory_PersistsKeys_AndSurvivesARestart()
    {
        string sealedText;
        using (ServiceProvider first = Build(this.dir))
        {
            sealedText = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("arun@anjal.co.in");
        }

        Assert.NotEmpty(System.IO.Directory.GetFiles(this.dir, "key-*.xml"));

        using ServiceProvider second = Build(this.dir);
        string opened = second.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Unprotect(sealedText);
        Assert.Equal("arun@anjal.co.in", opened);
    }

    [Fact]
    public void NoKeysDirectory_KeepsTheDefault_WithoutThrowing()
    {
        using ServiceProvider provider = Build(null);
        Assert.NotNull(provider.GetRequiredService<IDataProtectionProvider>());
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.dir))
        {
            System.IO.Directory.Delete(this.dir, recursive: true);
        }
    }

    private static ServiceProvider Build(string? keysDirectory)
    {
        var services = new ServiceCollection();
        Program.ConfigureDataProtection(services, keysDirectory);
        return services.BuildServiceProvider();
    }
}
