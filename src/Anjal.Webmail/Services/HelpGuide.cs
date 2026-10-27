using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>One part of the help guide (design review [8a]).</summary>
/// <param name="Id">Its key.</param>
/// <param name="Title">Its title.</param>
/// <param name="Icon">Its LiPicons name.</param>
public sealed record HelpPart(string Id, string Title, string Icon);

/// <summary>Where "Show me in Anjal" goes, and what it rings.</summary>
/// <param name="Path">The page.</param>
/// <param name="At">The CSS selector of the real control.</param>
/// <param name="Line">The one line shown beside it.</param>
public sealed record HelpShow(string Path, string At, string Line);

/// <summary>A picture in an article (rc.22): the flow chart of how an account is made.</summary>
/// <param name="Src">Its address under the webmail's static files.</param>
/// <param name="Caption">One line under it.</param>
public sealed record HelpFigure(string Src, string Caption);

/// <summary>One help article: one task (design review [8b]).</summary>
/// <param name="Id">Its key, in its address.</param>
/// <param name="Part">Its part.</param>
/// <param name="Title">The task, in the words on the screen.</param>
/// <param name="Summary">One or two sentences.</param>
/// <param name="Steps">The numbered steps.</param>
/// <param name="Tip">A tip, or null.</param>
/// <param name="Show">Show me in Anjal, or null.</param>
/// <param name="Related">Related articles' ids.</param>
/// <param name="Keywords">Other words people use for it.</param>
/// <param name="Release">The release it describes.</param>
/// <param name="Org">What the organisation may set about it, or null.</param>
/// <param name="Figure">A picture under the steps (rc.22), or null.</param>
public sealed record HelpArticle(string Id, string Part, string Title, string Summary, IReadOnlyList<string> Steps, string? Tip, HelpShow? Show, IReadOnlyList<string> Related, string Keywords, string Release, string? Org, HelpFigure? Figure = null)
{
    /// <summary>About how long it takes to read, in minutes (at least one).</summary>
    public int Minutes => Math.Max(1, (int)Math.Round((this.Summary.Length + this.Steps.Sum(s => s.Length) + (this.Tip?.Length ?? 0)) / 900.0));
}

/// <summary>
/// rc.18 (design review section 11 [8a]-[8b], owner 11 Oct 2026): the help guide - parts and
/// articles built into the program (Resources/Help/en.json), one task an article, in the exact
/// words on the screen. Lajna answers from it too, and cites the article.
/// </summary>
public sealed partial class HelpGuide
{
    private readonly Dictionary<string, HelpArticle> byId;
    private readonly Dictionary<string, Dictionary<string, HelpArticle>> drafts = new(StringComparer.Ordinal);

    /// <summary>Make a guide from its parts and articles.</summary>
    /// <param name="parts">The parts, in order.</param>
    /// <param name="articles">The articles, in order.</param>
    public HelpGuide(IReadOnlyList<HelpPart> parts, IReadOnlyList<HelpArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(articles);
        this.Parts = parts;
        this.Articles = articles;
        this.byId = articles.ToDictionary(a => a.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// rc.26 (item 78, DES-16 §11): an article in another language - a draft, not checked by a person
    /// yet, as the word list's - or null when it has none. Everything but the words is the English one's.
    /// </summary>
    /// <param name="id">The article.</param>
    /// <param name="language">ta, ml, hi, mr or gu.</param>
    /// <returns>The draft, or null.</returns>
    public HelpArticle? Draft(string? id, string? language) =>
        id is not null && language is not null && this.drafts.TryGetValue(language, out Dictionary<string, HelpArticle>? set) && set.TryGetValue(id, out HelpArticle? a) ? a : null;

    /// <summary>The languages that have drafts.</summary>
    public IReadOnlyCollection<string> DraftLanguages => this.drafts.Keys;

    /// <summary>Add one language's drafts (from Resources/Help/&lt;language&gt;.preview.json).</summary>
    /// <param name="language">The language.</param>
    /// <param name="json">The drafts: {"articles": [{id, title, summary, steps, tip, line, org}]}.</param>
    public void AddDrafts(string language, JsonElement json)
    {
        ArgumentNullException.ThrowIfNull(language);
        var set = new Dictionary<string, HelpArticle>(StringComparer.Ordinal);
        foreach (JsonElement d in json.GetProperty("articles").EnumerateArray())
        {
            if (this.Get(Str(d, "id")) is not HelpArticle en)
            {
                continue;
            }
            List<string> steps = d.TryGetProperty("steps", out JsonElement st) && st.ValueKind == JsonValueKind.Array
                ? st.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                : new List<string>();
            set[en.Id] = en with
            {
                Title = Str(d, "title") is { Length: > 0 } t ? t : en.Title,
                Summary = Str(d, "summary") is { Length: > 0 } sm ? sm : en.Summary,
                Steps = steps.Count == en.Steps.Count ? steps : en.Steps,
                Tip = en.Tip is null ? null : Str(d, "tip") is { Length: > 0 } tip ? tip : en.Tip,
                Show = en.Show is null ? null : en.Show with { Line = Str(d, "line") is { Length: > 0 } l ? l : en.Show.Line },
                Org = en.Org is null ? null : Str(d, "org") is { Length: > 0 } o ? o : en.Org,
            };
        }
        this.drafts[language] = set;
    }

    /// <summary>The parts, in order.</summary>
    public IReadOnlyList<HelpPart> Parts { get; }

    /// <summary>Every article, in order.</summary>
    public IReadOnlyList<HelpArticle> Articles { get; }

    /// <summary>The release whose articles are "New in this release".</summary>
    public string NewestRelease => this.Articles.Select(a => a.Release).OrderByDescending(r => r, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;

    /// <summary>Read the guide built into the program.</summary>
    /// <returns>The guide.</returns>
    public static HelpGuide Load()
    {
        Assembly assembly = typeof(HelpGuide).Assembly;
        string name = assembly.GetManifestResourceNames().First(n => n.EndsWith(".Resources.Help.en.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing help guide " + name);
        using JsonDocument doc = JsonDocument.Parse(stream);
        var parts = doc.RootElement.GetProperty("parts").EnumerateArray()
            .Select(p => new HelpPart(Str(p, "id"), Str(p, "title"), Str(p, "icon"))).ToList();
        var articles = new List<HelpArticle>();
        foreach (JsonElement a in doc.RootElement.GetProperty("articles").EnumerateArray())
        {
            HelpShow? show = a.TryGetProperty("show", out JsonElement s) && s.ValueKind == JsonValueKind.Object
                ? new HelpShow(Str(s, "path"), Str(s, "at"), Str(s, "line"))
                : null;
            articles.Add(new HelpArticle(Str(a, "id"), Str(a, "part"), Str(a, "title"), Str(a, "summary"),
                a.GetProperty("steps").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList(),
                a.TryGetProperty("tip", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                show,
                a.GetProperty("related").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList(),
                Str(a, "keywords"), Str(a, "release"),
                a.TryGetProperty("org", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
                a.TryGetProperty("figure", out JsonElement f) && f.ValueKind == JsonValueKind.Object ? new HelpFigure(Str(f, "src"), Str(f, "caption")) : null));
        }
        var guide = new HelpGuide(parts, articles);
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Resources.Help.", StringComparison.Ordinal) && n.EndsWith(".preview.json", StringComparison.Ordinal)))
        {
            string language = resource[(resource.IndexOf(".Resources.Help.", StringComparison.Ordinal) + ".Resources.Help.".Length)..^".preview.json".Length];
            using Stream draft = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing help drafts " + resource);
            using JsonDocument drafts = JsonDocument.Parse(draft);
            guide.AddDrafts(language, drafts.RootElement);
        }
        return guide;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary>An article by its id, or null.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The article.</returns>
    public HelpArticle? Get(string? id) => id is not null && this.byId.TryGetValue(id, out HelpArticle? a) ? a : null;

    /// <summary>A part by its id, or null.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The part.</returns>
    public HelpPart? Part(string? id) => this.Parts.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The articles that answer a question, best first. Each word of the question that is in an
    /// article's title counts most, then its keywords, then its summary and steps; small words
    /// ("how", "do", "I") count for nothing.
    /// </summary>
    /// <param name="question">What was asked.</param>
    /// <param name="max">At most this many.</param>
    /// <returns>The articles with their scores.</returns>
    public IReadOnlyList<(HelpArticle Article, double Score)> Search(string question, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(question);
        List<string> words = WordsOf(question).Where(w => !Small.Contains(w)).Distinct().ToList();
        if (words.Count == 0)
        {
            return Array.Empty<(HelpArticle, double)>();
        }
        var scored = new List<(HelpArticle, double)>();
        foreach (HelpArticle a in this.Articles)
        {
            HashSet<string> title = WordsOf(a.Title).ToHashSet(StringComparer.Ordinal);
            HashSet<string> keys = WordsOf(a.Keywords).ToHashSet(StringComparer.Ordinal);
            HashSet<string> body = WordsOf(a.Summary + " " + string.Join(" ", a.Steps)).ToHashSet(StringComparer.Ordinal);
            double score = 0;
            foreach (string w in words)
            {
                score += title.Contains(w) || title.Any(t => t.Length > 4 && w.Length > 4 && Stem(t) == Stem(w)) ? 3
                    : keys.Contains(w) ? 2.5
                    : body.Contains(w) || body.Any(t => t.Length > 4 && w.Length > 4 && Stem(t) == Stem(w)) ? 1
                    : 0;
            }
            if (score > 0)
            {
                scored.Add((a, score / words.Count));
            }
        }
        return scored.OrderByDescending(x => x.Item2).Take(max).ToList();
    }

    private static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "i", "me", "my", "to", "do", "does", "did", "how", "can", "could", "what", "where", "why", "is", "are", "it", "of", "in",
        "on", "for", "and", "or", "with", "please", "you", "your", "anjal", "lajna", "want", "would", "like", "need", "should", "this", "that", "be", "get",
    };

    /// <summary>The lower-case words of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its words.</returns>
    public static IEnumerable<string> WordsOf(string text) => WordRegex().Matches((text ?? string.Empty).ToLowerInvariant()).Select(m => m.Value);

    private static string Stem(string w) => w.EndsWith("ing", StringComparison.Ordinal) ? w[..^3]
        : w.EndsWith("es", StringComparison.Ordinal) ? w[..^2]
        : w.EndsWith('s') ? w[..^1]
        : w.EndsWith("ed", StringComparison.Ordinal) ? w[..^2]
        : w;

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\p{M}'-]*")]
    private static partial Regex WordRegex();
}
