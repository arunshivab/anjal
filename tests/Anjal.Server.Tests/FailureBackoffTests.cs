namespace Anjal.Server.Tests;

/// <summary>DEF-026: an outage is logged once, not every few seconds.</summary>
public class FailureBackoffTests
{
    private static readonly int[] ExpectedWaits = { 2, 4, 8, 16, 32, 60, 60, 60, 60, 60 };

    [Fact]
    public void AnOutage_IsLoggedOnce_WaitsGrow_AndRecoveryIsLoggedOnce()
    {
        var lines = new List<string>();
        var backoff = new FailureBackoff("webhook worker", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), lines.Add);
        var down = new InvalidOperationException("Failed to connect to 127.0.0.1:5432");

        var waits = new List<TimeSpan>();
        for (int i = 0; i < 10; i++)
        {
            waits.Add(backoff.Failed(down));
        }

        Assert.Single(lines);
        Assert.Contains("Failed to connect", lines[0], StringComparison.Ordinal);
        Assert.Equal(ExpectedWaits, waits.Select(w => (int)w.TotalSeconds));

        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Succeeded());
        Assert.Equal(2, lines.Count);
        Assert.Contains("working again after 10", lines[1], StringComparison.Ordinal);

        backoff.Succeeded();
        Assert.Equal(2, lines.Count); // steady state is silent
    }
}
