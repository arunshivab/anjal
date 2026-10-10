using System.Security.Cryptography;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// DES-11 S6 (owner, 10 Oct 2026, "A"): the mail server makes its seal key once, publishes the
/// public half, locks every DKIM key still kept unlocked, and opens sealed keys only to sign.
/// </summary>
[Collection("ServerProcessSettings")]
public sealed class Des11SealKeyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "anjal-seal-" + Guid.NewGuid().ToString("N"));
    private readonly string? before = Environment.GetEnvironmentVariable("ANJAL_DKIM_SEAL_KEY");

    public Des11SealKeyTests()
    {
        Environment.SetEnvironmentVariable("ANJAL_DKIM_SEAL_KEY", Path.Combine(this.dir, "dkim-seal.pem"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ANJAL_DKIM_SEAL_KEY", this.before);
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheSealKey_IsMadeOnce_Published_LocksPlainKeys_AndTheSignerOpensThem()
    {
        var store = new InMemoryMessageStore();
        using RSA dkim = RSA.Create(2048);
        string pem = dkim.ExportPkcs8PrivateKeyPem();
        await store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "anjal.co.in", Selector = "anjal", PrivateKeyPem = pem });
        var log = new List<string>();

        using RSA? first = await DkimSealKey.PrepareAsync(store, log.Add);
        Assert.NotNull(first);
        Assert.True(File.Exists(Path.Combine(this.dir, "dkim-seal.pem")));
        SealRecord record = (await KeySeal.ReadRecordAsync(store))!;
        Assert.Equal(Convert.ToBase64String(first!.ExportSubjectPublicKeyInfo()), record.PublicKey);
        DkimKeyRow stored = (await store.GetDkimKeyAsync("anjal.co.in"))!;
        Assert.True(KeySeal.IsSealed(stored.PrivateKeyPem));
        Assert.Contains(log, l => l.Contains("1 DKIM key(s)", StringComparison.Ordinal));

        // The next start reads the same key and changes nothing.
        using RSA? again = await DkimSealKey.PrepareAsync(store, log.Add);
        Assert.Equal(first.ExportSubjectPublicKeyInfo(), again!.ExportSubjectPublicKeyInfo());

        Anjal.Dkim.DkimKey? signing = await new StoreBackedDkimResolver(store, again).ResolveAsync("anjal.co.in");
        Assert.Equal(pem, signing!.PrivateKeyPem);
        Assert.Null(await new StoreBackedDkimResolver(store).ResolveAsync("anjal.co.in"));
    }
}
