using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

public class FailClosedPolicyTests
{
    /// <summary>A mailbox store whose every call fails, as during a database outage.</summary>
    public class BrokenStore : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new System.InvalidOperationException("connection refused");
    }

    private static IMailboxStore Broken() => System.Reflection.DispatchProxy.Create<IMailboxStore, BrokenStore>();

    [Fact]
    public async System.Threading.Tasks.Task Quota_WhenTheStoreFails_Defers451_NotAllows()
    {
        PolicyDecision d = await new QuotaPolicy(Broken()).OnRcptToAsync("203.0.113.1", null, "a@b.test", "u@q.test");
        Assert.False(d.Allowed);
        Assert.Equal(451, d.ReplyCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task TenantState_WhenTheStoreFails_Defers451_NotAllows()
    {
        PolicyDecision d = await new TenantStatePolicy(Broken()).OnRcptToAsync("203.0.113.1", null, "a@b.test", "u@q.test");
        Assert.False(d.Allowed);
        Assert.Equal(451, d.ReplyCode);
    }
}
