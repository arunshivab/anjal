using System.Text.RegularExpressions;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15 (owner, 7 Oct 2026): a correction is made for the whole webmail, not for the one place it
/// was seen. These tests read the repository and hold each such rule everywhere, so a new screen
/// or menu cannot bring a fault back.
/// </summary>
public partial class SystemWideTests
{
    private static readonly string[] ScreenChecks = { "noscroll", "names", "tips", "menus", "close", "align", "shrink", "clip", "same", "steady", "lineup", "sizes", "scrollbars", "flap", "swap", "keyboard", "reach" };

    private static readonly HashSet<string> OptionalHooks = new(StringComparer.Ordinal) { "masonry", "bg-name" };

    /// <summary>
    /// "From a template" and "Small window" in the Compose arrow failed (415) because their forms
    /// were not sent as file uploads, which the endpoint they post to requires. Every form that
    /// posts to an endpoint taking files must be sent as one.
    /// </summary>
    [Fact]
    public void EveryFormPostingToAnEndpointThatTakesFiles_IsSentAsAFileUpload()
    {
        string root = RepoRoot();
        string web = Path.Combine(root, "src", "Anjal.Webmail");
        var fileEndpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (string cs in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string line in File.ReadLines(cs))
            {
                Match m = MapPost().Match(line);
                if (m.Success && line.Contains("IFormFile", StringComparison.Ordinal))
                {
                    fileEndpoints.Add(m.Groups[1].Value);
                }
            }
        }
        Assert.Contains("/draft", fileEndpoints);
        Assert.Contains("/compose", fileEndpoints);

        var wrong = new List<string>();
        foreach (string razor in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match form in FormTag().Matches(File.ReadAllText(razor)))
            {
                Match action = ActionAttr().Match(form.Value);
                if (action.Success && fileEndpoints.Contains(action.Groups[1].Value)
                    && !form.Value.Contains("enctype=\"multipart/form-data\"", StringComparison.Ordinal))
                {
                    wrong.Add(Path.GetFileName(razor) + ": " + form.Value);
                }
            }
        }
        Assert.True(wrong.Count == 0, "Forms to file-taking endpoints without multipart: " + string.Join(" | ", wrong));
    }

    /// <summary>
    /// Menus that stayed open on a click outside: the close rule named only some menus, and a click
    /// in an open message's text (its own frame) never reached the page. The rule must cover every
    /// pop-up menu and the frame.
    /// </summary>
    [Fact]
    public void EveryPopUpMenu_ClosesOnAClickOutside_IncludingInsideAMessage()
    {
        string js = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.js"));
        Assert.Contains("function openPopups()", js, StringComparison.Ordinal);
        Assert.Contains("$$(\"details[open]\").filter(function (d) { return !!popupBody(d); })", js, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"blur\"", js, StringComparison.Ordinal);
        Assert.DoesNotContain("var MENUS =", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Compose arrow jumped when clicked: it was placed with a shift that was taken off while its
    /// menu was open. It is placed without any shift.
    /// </summary>
    [Fact]
    public void TheComposeArrow_IsPlacedWithoutAShift()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        foreach (string line in css.Split('\n'))
        {
            if (line.Contains(".composemore", StringComparison.Ordinal) && !line.Contains("pmenu-body", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("transform", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A card that had finished appearing kept its animation's end position, which made it a frame
    /// that held and cut off its menus (Contacts, Export). Entrance animations end clean everywhere.
    /// </summary>
    [Fact]
    public void EntranceAnimations_EndClean_SoNothingHoldsItsMenus()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        Assert.DoesNotMatch(KeptAnimation(), css);
    }

    /// <summary>
    /// Mark all as read posted to "/null": its button moved into its own form, and the script
    /// still looked for it where it had been. Everything the script looks for on a page must
    /// still be on a page. (The dashboards' stacking and the mail-background label are optional
    /// by design: the script checks for them and does nothing when they are absent.)
    /// </summary>
    [Fact]
    public void EverythingTheScriptLooksFor_IsStillOnAPage()
    {
        string root = RepoRoot();
        string web = Path.Combine(root, "src", "Anjal.Webmail");
        string js = File.ReadAllText(Path.Combine(web, "wwwroot", "app.js"));
        var markup = new System.Text.StringBuilder();
        foreach (string f in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories))
        {
            if ((f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                markup.Append(File.ReadAllText(f));
            }
        }
        string all = markup.ToString();
        var missing = new List<string>();
        foreach (Match m in ScriptHook().Matches(js))
        {
            string attr = "data-" + m.Groups[1].Value;
            // Also fine: what the script makes itself (written into markup it builds, or set as an attribute).
            bool madeByScript = js.Replace("[" + attr, string.Empty, StringComparison.Ordinal).Contains(attr, StringComparison.Ordinal);
            if (!all.Contains(attr, StringComparison.Ordinal) && !madeByScript && !OptionalHooks.Contains(m.Groups[1].Value))
            {
                missing.Add(attr);
            }
        }
        Assert.True(missing.Count == 0, "app.js looks for what no page has: " + string.Join(", ", missing.Distinct()));
    }

    /// <summary>
    /// The script that posts a button in the background must find where to post even when the
    /// button carries no address of its own (it is then its form's): Mark all as read posted to
    /// "/null" when its button moved into a form of its own.
    /// </summary>
    [Fact]
    public void ScriptsThatPostAButton_FallBackToItsFormsAddress()
    {
        string js = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.js"));
        foreach (Match m in FormactionRead().Matches(js))
        {
            Assert.StartsWith(" ||", js[(m.Index + m.Length)..], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Item 47 and UX-09 (owner, 7 Oct 2026): the browser checks of every screen - no page
    /// scrolling on mail screens at 1366x768, names, tips, menus, closing, alignment, squeezing,
    /// cut-off words, the keyboard and every button - run on every change, as one of the GitHub
    /// checks; and the word list is checked there too, not only in the local PowerShell block.
    /// </summary>
    [Fact]
    public void TheScreenAndWordChecks_RunOnEveryChange()
    {
        string root = RepoRoot();
        string screens = File.ReadAllText(Path.Combine(root, ".github", "workflows", "screens.yml"));
        Assert.Contains("pull_request:", screens, StringComparison.Ordinal);
        Assert.Contains("tools/ui/setup.sh", screens, StringComparison.Ordinal);
        Assert.Contains("run: node check.js", screens, StringComparison.Ordinal);
        string runner = File.ReadAllText(Path.Combine(root, "tools", "ui", "check.js"));
        string readme = File.ReadAllText(Path.Combine(root, "tools", "ui", "README.md"));
        foreach (string check in ScreenChecks)
        {
            Assert.True(File.Exists(Path.Combine(root, "tools", "ui", "checks", check + ".js")), check);
            Assert.Contains("'" + check + "'", runner, StringComparison.Ordinal);
            Assert.Contains("| `" + check + "` |", readme, StringComparison.Ordinal);
        }
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tools", "ui", "checks"), "*.js"))
        {
            Assert.Contains(Path.GetFileNameWithoutExtension(file), ScreenChecks);
        }
        string style = File.ReadAllText(Path.Combine(root, ".github", "workflows", "style.yml"));
        Assert.Contains("python tools/check_words.py", style, StringComparison.Ordinal);
        Assert.Contains("python tools/gen_words.py", style, StringComparison.Ordinal);
        Assert.Contains("python tools/check_contrast.py", style, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test that changes a setting the whole process reads (Environment.SetEnvironmentVariable)
    /// must not run beside other tests: on the owner's laptop on 8 Oct 2026 an operator test made
    /// an ordinary person's language page show the operators' preview. Every such test class is in
    /// a collection that runs alone, in every test project.
    /// </summary>
    [Fact]
    public void TestsThatChangeProcessSettings_RunAlone()
    {
        string tests = Path.Combine(RepoRoot(), "tests");
        string all = string.Join("\n", Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(File.ReadAllText));
        var alone = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AloneCollection().Matches(all))
        {
            alone.Add(m.Groups[1].Value);
        }
        var wrong = new List<string>();
        foreach (string f in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !text.Contains("Environment.SetEnvironmentVariable(", StringComparison.Ordinal)
                || text.Contains("[ModuleInitializer]", StringComparison.Ordinal))
            {
                continue;
            }
            Match c = InCollection().Match(text);
            if (!c.Success || !alone.Contains(c.Groups[1].Value))
            {
                wrong.Add(Path.GetFileName(f));
            }
        }
        Assert.True(wrong.Count == 0, "Change process settings but may run beside other tests: " + string.Join(", ", wrong));
    }

    /// <summary>
    /// Owner, 8 Oct 2026: previous to the left of the page numbers and next to the right had been
    /// corrected for the folder list, and the search results still had their own, older steps. Every
    /// list with pages uses the one PageSteps; no page draws its own.
    /// </summary>
    [Fact]
    public void EveryListWithPages_UsesTheOnePageSteps()
    {
        string components = Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "Components");
        var own = new List<string>();
        foreach (string f in Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) == "PageSteps.razor")
            {
                continue;
            }
            string text = File.ReadAllText(f);
            if (text.Contains("pgbtn", StringComparison.Ordinal) || text.Contains("L[\"Newer\"]", StringComparison.Ordinal)
                || text.Contains("L[\"Older\"]", StringComparison.Ordinal) || (text.Contains("class=\"pager", StringComparison.Ordinal) && !text.Contains("<PageSteps", StringComparison.Ordinal)))
            {
                own.Add(Path.GetFileName(f));
            }
        }
        Assert.True(own.Count == 0, "Draw their own page steps: " + string.Join(", ", own));
    }

    /// <summary>
    /// DES-11 F2 (owner, 10 Oct 2026, system-wide): Suspend and Resume in the Anjal console took
    /// posts from any website, because the framework checks only posts that bind form fields.
    /// Every post the webmail answers must be checked: by binding a form field or file, by the
    /// RequireAntiforgery filter, or by validating itself.
    /// </summary>
    [Fact]
    public void EveryPost_IsCheckedAgainstPostsFromOtherWebsites()
    {
        string web = Path.Combine(RepoRoot(), "src", "Anjal.Webmail");
        var open = new List<string>();
        int posts = 0;
        foreach (string cs in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string part in MapCall().Split(File.ReadAllText(cs)))
            {
                Match m = MapPost().Match(part);
                if (!m.Success || !part.StartsWith("app.MapPost(", StringComparison.Ordinal))
                {
                    continue;
                }
                posts++;
                if (!Checked().IsMatch(part))
                {
                    open.Add(Path.GetFileName(cs) + ": " + m.Groups[1].Value);
                }
            }
        }
        Assert.True(posts > 100, "Too few posts found: " + posts);
        Assert.True(open.Count == 0, "Posts with no check against other websites: " + string.Join(" | ", open));
    }

    /// <summary>
    /// DES-11 F3 (owner, 10 Oct 2026, system-wide): ANJAL_OPERATORS, without which nobody can open
    /// the Anjal console, was described nowhere, nor were 34 other settings. Every ANJAL_ setting
    /// the code reads must be described in the README, and the operators must be in the webmail's
    /// example settings file.
    /// </summary>
    [Fact]
    public void EverySettingTheCodeReads_IsDescribed()
    {
        string root = RepoRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var read = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string cs in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match m in SettingName().Matches(File.ReadAllText(cs)))
            {
                read.Add(m.Groups[1].Value);
            }
        }
        Assert.True(read.Count > 80, "Too few settings found: " + read.Count);
        string[] missing = read.Where(s => !readme.Contains("`" + s + "`", StringComparison.Ordinal) && !readme.Contains(s + "=", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "Settings the README does not describe: " + string.Join(", ", missing));
        Assert.Contains("\nANJAL_OPERATORS=", File.ReadAllText(Path.Combine(root, "deploy", "webmail.env.example")).Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// DES-11 F10 (owner, 10 Oct 2026, system-wide): mailbox fullness and four other figures
    /// still rounded down by hand after the 9 Oct rule that percentages round one way everywhere.
    /// A percentage people read is made only by Sizes.Percent; working one out by hand is left to
    /// drawing bars (which keep a smallest size) and the processor load.
    /// </summary>
    [Fact]
    public void EveryPercentagePeopleRead_IsRoundedOneWay()
    {
        string web = Path.Combine(RepoRoot(), "src", "Anjal.Webmail");
        var byHand = new List<string>();
        foreach (string f in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories))
        {
            if ((!f.EndsWith(".cs", StringComparison.Ordinal) && !f.EndsWith(".razor", StringComparison.Ordinal))
                || f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || f.EndsWith("Sizes.cs", StringComparison.Ordinal))
            {
                continue;
            }
            int n = 0;
            foreach (string line in File.ReadLines(f))
            {
                n++;
                bool hand = HundredTimes().IsMatch(line);
                if (hand && !line.Contains("Math.Max(", StringComparison.Ordinal) && !line.Contains("cores", StringComparison.Ordinal))
                {
                    byHand.Add(Path.GetFileName(f) + ":" + n);
                }
            }
        }
        Assert.True(byHand.Count == 0, "Percentages worked out by hand: " + string.Join(", ", byHand));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("Map(?:Post)\\(\"([^\"]+)\"")]
    private static partial Regex MapPost();

    // A whole setting name in quotes; a prefix ending in "_" (a name check) is not a setting.
    [GeneratedRegex("\"(ANJAL_[A-Z0-9_]*[A-Z0-9])\"")]
    private static partial Regex SettingName();

    [GeneratedRegex("\\* ?100(?:d|\\.0)? ?/")]
    private static partial Regex HundredTimes();

    [GeneratedRegex("(?=\\bapp\\.Map\\w*\\()")]
    private static partial Regex MapCall();

    [GeneratedRegex("\\[FromForm\\]|IFormFile|IFormCollection|IAntiforgery|RequireAntiforgery")]
    private static partial Regex Checked();

    [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex("action=\"([^\"?]+)\"")]
    private static partial Regex ActionAttr();

    [GeneratedRegex("getAttribute\\(\"formaction\"\\)")]
    private static partial Regex FormactionRead();

    [GeneratedRegex("\\[data-([a-z0-9-]+)")]
    private static partial Regex ScriptHook();

    [GeneratedRegex("\\[CollectionDefinition\\(\"([^\"]+)\",\\s*DisableParallelization\\s*=\\s*true\\)\\]")]
    private static partial Regex AloneCollection();

    [GeneratedRegex("\\[Collection\\(\"([^\"]+)\"\\)\\]")]
    private static partial Regex InCollection();

    [GeneratedRegex("animation:\\s*anjal-(?:rise|pop)\\b[^;}]*\\bboth\\b")]
    private static partial Regex KeptAnimation();
}
