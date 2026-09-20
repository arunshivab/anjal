namespace Anjal.Smtp.Tests;

public class CountersTests
{
    [Fact]
    public void Increment_Add_Get()
    {
        string name = "anjal_test_counter_" + System.Guid.NewGuid().ToString("N");
        Assert.Equal(0, Counters.Get(name));
        Counters.Increment(name);
        Counters.Add(name, 4);
        Assert.Equal(5, Counters.Get(name));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Counters.Add(name, -1));
    }

    [Fact]
    public void RenderPrometheus_IncludesCountersAndGauges_WithHelpAndType()
    {
        string counter = "anjal_test_render_" + System.Guid.NewGuid().ToString("N");
        string gauge = "anjal_test_gauge_" + System.Guid.NewGuid().ToString("N");
        Counters.Increment(counter);
        Counters.Describe(counter, "A test counter.");
        Counters.RegisterGauge(gauge, "A test gauge.", () => 2.5);
        try
        {
            string text = Counters.RenderPrometheus();
            Assert.Contains($"# HELP {counter} A test counter.\n# TYPE {counter} counter\n{counter} 1\n", text, System.StringComparison.Ordinal);
            Assert.Contains($"# HELP {gauge} A test gauge.\n# TYPE {gauge} gauge\n{gauge} 2.5\n", text, System.StringComparison.Ordinal);
        }
        finally
        {
            Counters.UnregisterGauge(gauge);
        }
        Assert.DoesNotContain(gauge, Counters.RenderPrometheus(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void FailingGauge_RendersNaN_NotThrow()
    {
        string gauge = "anjal_test_bad_gauge_" + System.Guid.NewGuid().ToString("N");
        Counters.RegisterGauge(gauge, "boom", () => throw new System.InvalidOperationException());
        try
        {
            Assert.Contains($"{gauge} NaN", Counters.RenderPrometheus(), System.StringComparison.Ordinal);
        }
        finally
        {
            Counters.UnregisterGauge(gauge);
        }
    }
}
