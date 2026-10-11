using Anjal.Webmail.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anjal.Webmail.Tests;

public class Rc11StampTests
{
    private static async System.Threading.Tasks.Task<string> RenderAsync(string word, bool locked)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Word"] = word, ["Lock"] = locked, ["Why"] = "why" });
            return (await renderer.RenderComponentAsync<Stamp>(parameters)).ToHtmlString();
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task TheStamp_IsTheApprovedOption2_WordsOnTwoLines_PerforatedEdge_GreenPanel()
    {
        // SPEC-11 item 49 (option 2, owner 2 Oct).
        string html = await RenderAsync("Verified sender", locked: false);
        Assert.Contains(">Verified</text>", html, System.StringComparison.Ordinal);
        Assert.Contains(">sender</text>", html, System.StringComparison.Ordinal);
        Assert.Equal(44, System.Text.RegularExpressions.Regex.Count(html, "class=\"st-bite\""));
        Assert.Contains("class=\"st-panel\" x=\"6\" y=\"6\" width=\"56\" height=\"56\"", html, System.StringComparison.Ordinal);
        Assert.Contains("d=\"M-9 0 L-3 6 L9 -6\"", html, System.StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Verified sender\"", html, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task ArrivedEncrypted_HasALockInsteadOfATick()
    {
        string html = await RenderAsync("Arrived encrypted", locked: true);
        Assert.DoesNotContain("M-9 0 L-3 6 L9 -6", html, System.StringComparison.Ordinal);
        Assert.Contains("a4 4 0 0 1 8 0", html, System.StringComparison.Ordinal);
        Assert.Contains(">encrypted</text>", html, System.StringComparison.Ordinal);
    }
}
