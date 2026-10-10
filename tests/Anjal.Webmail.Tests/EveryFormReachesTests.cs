using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Anjal.Webmail.Tests;

/// <summary>
/// DES-11 F1 (owner, 10 Oct 2026: "fix ... and also a system wide check"): the rules and folders
/// addresses were written in rc.12 but never connected, so saving a rule or creating a folder
/// answered 400 for three releases. This holds every screen to it: each address a form posts to,
/// on every screen, must be an address the webmail answers.
/// </summary>
public sealed partial class EveryFormReachesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-reach-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task EveryFormOnEveryScreen_PostsToAnAddressTheWebmailAnswers()
    {
        WebApplication app = Program.CreateApp(Array.Empty<string>(), new InMemoryMessageStore(), new MaildirStore(this.root, "test"), "anjal.localhost", "http://127.0.0.1:0");
        await using (app)
        {
            var posts = new List<string[]>();
            foreach (EndpointDataSource source in ((IEndpointRouteBuilder)app).DataSources)
            {
                foreach (RouteEndpoint e in source.Endpoints.OfType<RouteEndpoint>())
                {
                    // A page itself also takes posts (Blazor's own form handling), and answers a form it
                    // has no handler for with 400 - which is how F1 hid: /settings/{Section} took them.
                    bool page = e.Metadata.Any(m => m.GetType().Name == "ComponentTypeMetadata");
                    HttpMethodMetadata? methods = e.Metadata.GetMetadata<HttpMethodMetadata>();
                    if (!page && methods is not null && methods.HttpMethods.Contains("POST"))
                    {
                        posts.Add(Segments(e.RoutePattern.RawText ?? string.Empty));
                    }
                }
            }
            Assert.NotEmpty(posts);

            string web = Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "Components");
            var unanswered = new SortedSet<string>(StringComparer.Ordinal);
            int seen = 0;
            foreach (string razor in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
            {
                foreach (Match m in Action().Matches(File.ReadAllText(razor)))
                {
                    // A form sent with GET opens a page (search, the console lists); only posts are held here.
                    string action = m.Groups[2].Value;
                    if (!action.StartsWith('/') || GetMethod().IsMatch(m.Groups[0].Value))
                    {
                        continue;
                    }
                    seen++;
                    string[] wanted = Segments(action);
                    if (!posts.Any(p => Fits(p, wanted)))
                    {
                        unanswered.Add(Path.GetFileName(razor) + ": " + action);
                    }
                }
            }
            Assert.True(seen > 100, "Too few forms found: " + seen);
            Assert.True(unanswered.Count == 0, "Forms posting to an address nobody answers: " + string.Join(" | ", unanswered));
        }
    }

    private static string[] Segments(string path)
    {
        int q = path.IndexOf('?', StringComparison.Ordinal);
        return (q >= 0 ? path[..q] : path).Trim('/').Split('/');
    }

    // A route part in braces takes any value; a razor part starting with @ is filled at run time.
    private static bool Fits(string[] route, string[] form) =>
        route.Length == form.Length && route.Zip(form).All(x => x.First.StartsWith('{') || x.Second.StartsWith('@') || string.Equals(x.First, x.Second, StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("<(form|button|input)\\b[^>]*?\\b(?:form)?action=\"([^\"]+)\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Action();

    [GeneratedRegex("\\b(?:form)?method=\"get\"", RegexOptions.IgnoreCase)]
    private static partial Regex GetMethod();
}
