using System.Reflection;

namespace Anjal.Smtp.Tests;

/// <summary>
/// DEF-065: <see cref="DeliveryContext.WithRawBytes"/> keeps every property.
/// Every property is given a non-default value by reflection, so a property
/// added later and forgotten in the copy fails this test.
/// </summary>
public sealed class Def065DeliveryContextCopyTests
{
    private static readonly byte[] Original = { 1, 2, 3 };
    private static readonly byte[] Replaced = { 9, 9 };

    [Fact]
    public void WithRecipients_KeepsEveryProperty_AndReplacesOnlyTheRecipients()
    {
        var source = new DeliveryContext();
        PropertyInfo[] properties = typeof(DeliveryContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo p in properties)
        {
            p.SetValue(source, SampleFor(p));
        }
        string[] replaced = { "someone-else@example.net" };

        DeliveryContext copy = source.WithRecipients(replaced);

        foreach (PropertyInfo p in properties)
        {
            if (p.Name == nameof(DeliveryContext.EnvelopeTo))
            {
                Assert.Same(replaced, copy.EnvelopeTo);
            }
            else
            {
                Assert.Equal(p.GetValue(source), p.GetValue(copy));
            }
        }
        Assert.Contains(properties, p => p.Name == nameof(DeliveryContext.EvidenceId));
    }

    private static object SampleFor(PropertyInfo p) =>
        p.PropertyType == typeof(string) ? "value-of-" + p.Name
        : p.PropertyType == typeof(byte[]) ? Original
        : p.PropertyType == typeof(IReadOnlyList<string>) ? new[] { "rcpt-" + p.Name }
        : p.PropertyType == typeof(object) ? new object()
        : p.PropertyType == typeof(System.Guid?) ? System.Guid.NewGuid()
        : p.PropertyType == typeof(bool) ? true
        : throw new InvalidOperationException("Give the test a sample value for " + p.Name + " (" + p.PropertyType + ")");

    [Fact]
    public void WithRawBytes_KeepsEveryProperty_AndReplacesOnlyTheBytes()
    {
        var source = new DeliveryContext();
        PropertyInfo[] properties = typeof(DeliveryContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo p in properties)
        {
            object value = p.PropertyType == typeof(string) ? "value-of-" + p.Name
                : p.PropertyType == typeof(byte[]) ? Original
                : p.PropertyType == typeof(IReadOnlyList<string>) ? new[] { "rcpt-" + p.Name }
                : p.PropertyType == typeof(object) ? new object()
                : p.PropertyType == typeof(System.Guid?) ? System.Guid.NewGuid()
                : p.PropertyType == typeof(bool) ? true
                : throw new InvalidOperationException("Give the test a sample value for " + p.Name + " (" + p.PropertyType + ")");
            p.SetValue(source, value);
        }

        DeliveryContext copy = source.WithRawBytes(Replaced);

        foreach (PropertyInfo p in properties)
        {
            if (p.Name == nameof(DeliveryContext.RawBytes))
            {
                Assert.Same(Replaced, copy.RawBytes);
            }
            else
            {
                Assert.Equal(p.GetValue(source), p.GetValue(copy));
            }
        }
        Assert.Contains(properties, p => p.Name == nameof(DeliveryContext.TransportTls));
    }
}
