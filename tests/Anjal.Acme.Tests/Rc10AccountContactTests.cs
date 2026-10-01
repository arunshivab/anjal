namespace Anjal.Acme.Tests;

/// <summary>
/// v1.0.0-rc.10: a changed ANJAL_ACME_EMAIL reaches an existing account. An
/// existing account keeps the contact it was created with unless updated
/// (RFC 8555 7.3.2), so changing the setting alone did nothing.
/// </summary>
public sealed class Rc10AccountContactTests : System.IDisposable
{
    private readonly FakeAcmeServer ca = new(0);

    public Rc10AccountContactTests()
    {
        this.ca.Start();
    }

    public void Dispose() => this.ca.Dispose();

    [Fact]
    public async Task ChangedContact_UpdatesTheExistingAccount_UnchangedSendsNothing()
    {
        using AccountKey key = AccountKey.Create();
        var first = new AcmeClient(this.ca.DirectoryUrl, key);
        string url = await first.EnsureAccountAsync("arun@anjal.co.in");
        Assert.Equal("mailto:arun@anjal.co.in", this.ca.ContactOf(url));
        Assert.Equal(0, this.ca.AccountUpdates);

        var second = new AcmeClient(this.ca.DirectoryUrl, key);
        Assert.Equal(url, await second.EnsureAccountAsync("ops@anjal.co.in"));
        Assert.Equal("mailto:ops@anjal.co.in", this.ca.ContactOf(url));
        Assert.Equal(1, this.ca.AccountUpdates);

        var third = new AcmeClient(this.ca.DirectoryUrl, key);
        await third.EnsureAccountAsync("ops@anjal.co.in");
        Assert.Equal(1, this.ca.AccountUpdates);
    }
}
