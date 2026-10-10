namespace Anjal.Store.Tests;

/// <summary>rc.13: an organisation's own documents (its sign-in look, later its policies).</summary>
public sealed class Rc13TenantDocumentTests
{
    [Fact]
    public async Task TenantDocuments_AreKeptPerOrganisationAndKind()
    {
        var store = new InMemoryMessageStore();
        var a = System.Guid.NewGuid();
        var b = System.Guid.NewGuid();
        Assert.Null(await store.GetTenantDocumentAsync(a, "branding"));
        await store.SetTenantDocumentAsync(a, "branding", "{\"Logo\":\"x\"}");
        await store.SetTenantDocumentAsync(a, "branding", "{\"Logo\":\"y\"}");
        await store.SetTenantDocumentAsync(b, "policy", "{}");
        Assert.Equal("{\"Logo\":\"y\"}", await store.GetTenantDocumentAsync(a, "branding"));
        Assert.Null(await store.GetTenantDocumentAsync(b, "branding"));
        Assert.Equal("{}", await store.GetTenantDocumentAsync(b, "policy"));
    }
}
