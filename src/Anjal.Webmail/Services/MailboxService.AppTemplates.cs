using System.Net;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>One language of an application template: subject, text and HTML with {{placeholders}}.</summary>
public sealed class AppTemplateText
{
    /// <summary>The subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The HTML.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>True once a person checked this language's version (English always is).</summary>
    public bool Checked { get; set; }
}

/// <summary>One saved version of an application template.</summary>
public sealed class AppTemplateVersion
{
    /// <summary>Its number, from 1.</summary>
    public int Version { get; set; }

    /// <summary>When it was saved.</summary>
    public DateTimeOffset Saved { get; set; }

    /// <summary>Who saved it.</summary>
    public string By { get; set; } = string.Empty;

    /// <summary>Its languages: en, ta, ml, hi, mr, gu.</summary>
    public Dictionary<string, AppTemplateText> Languages { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>rc.26 (DES-14 §22, D-225): a template for applications, with its last 10 versions.</summary>
public sealed class AppTemplate
{
    /// <summary>Its name, such as signin-code.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its versions, oldest first; the last is current.</summary>
    public List<AppTemplateVersion> Versions { get; set; } = new();

    /// <summary>The current version.</summary>
    public AppTemplateVersion? Current => this.Versions.Count == 0 ? null : this.Versions[^1];
}

/// <summary>
/// rc.26 (DES-14 §22): the organisation's templates for applications, kept as its "app-templates"
/// document. In a send, "template" takes the place of subject, text and html; the language chooses
/// the version (English when missing); values are inserted as text - escaped in HTML.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The organisation document holding the templates for applications.</summary>
    public const string AppTemplatesKind = "app-templates";

    /// <summary>The languages a template may have.</summary>
    public static readonly IReadOnlyList<string> AppTemplateLanguages = new[] { "en", "ta", "ml", "hi", "mr", "gu" };

    private static readonly Regex PlaceholderPattern = new(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex TemplateNamePattern = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The organisation's templates for applications.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The templates.</returns>
    public async Task<List<AppTemplate>> AppTemplatesAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<List<AppTemplate>>(tenantId, AppTemplatesKind, ct).ConfigureAwait(false) ?? new List<AppTemplate>();

    /// <summary>Save a language of a template as a new version (the last 10 are kept); the error, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="name">The template.</param>
    /// <param name="language">The language.</param>
    /// <param name="text">The subject, text and HTML.</param>
    /// <param name="by">Who saves it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SaveAppTemplateAsync(Guid tenantId, string name, string language, AppTemplateText text, string by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(by);
        string n = name.Trim().ToLowerInvariant();
        if (!TemplateNamePattern.IsMatch(n))
        {
            return "A template's name is short: small letters, digits, _ . and -, such as signin-code.";
        }
        if (!AppTemplateLanguages.Contains(language))
        {
            return "Choose a language.";
        }
        if (text.Text.Trim().Length == 0 && text.Html.Trim().Length == 0)
        {
            return "Write the text, the HTML, or both.";
        }
        List<AppTemplate> all = await this.AppTemplatesAsync(tenantId, ct).ConfigureAwait(false);
        AppTemplate? t = all.Find(x => x.Name == n);
        if (t is null)
        {
            if (language != "en")
            {
                return "Write the English version first; the other languages follow it.";
            }
            t = new AppTemplate { Name = n };
            all.Add(t);
        }
        var version = new AppTemplateVersion
        {
            Version = (t.Current?.Version ?? 0) + 1,
            Saved = DateTimeOffset.UtcNow,
            By = by,
            Languages = t.Current is AppTemplateVersion cur ? cur.Languages.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) : new Dictionary<string, AppTemplateText>(StringComparer.Ordinal),
        };
        version.Languages[language] = new AppTemplateText
        {
            Subject = text.Subject.Trim(),
            Text = text.Text,
            Html = text.Html,
            Checked = language == "en" || text.Checked,
        };
        t.Versions.Add(version);
        if (t.Versions.Count > 10)
        {
            t.Versions.RemoveRange(0, t.Versions.Count - 10);
        }
        await this.WriteTenantDocumentAsync(tenantId, AppTemplatesKind, all, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a template.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="name">The template.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was there.</returns>
    public async Task<bool> RemoveAppTemplateAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        List<AppTemplate> all = await this.AppTemplatesAsync(tenantId, ct).ConfigureAwait(false);
        if (all.RemoveAll(t => t.Name == name) == 0)
        {
            return false;
        }
        await this.WriteTenantDocumentAsync(tenantId, AppTemplatesKind, all, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A template filled in (DES-14 §22): the subject, text and HTML of the language asked for (English
    /// when it has none), the version used; or the failure - template_not_found (404), or
    /// template_value_missing (422) naming the placeholder.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="language">The language, or empty.</param>
    /// <param name="values">The values.</param>
    /// <returns>The filled-in text and version, or the failure's code and the missing name.</returns>
    public static (string Subject, string Text, string Html, int Version, string? Failure, string Missing) FillAppTemplate(AppTemplate? template, string language, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(values);
        if (template?.Current is not AppTemplateVersion v)
        {
            return (string.Empty, string.Empty, string.Empty, 0, "template_not_found", string.Empty);
        }
        AppTemplateText text = v.Languages.TryGetValue(language.Length > 0 ? language : "en", out AppTemplateText? chosen) ? chosen
            : v.Languages.TryGetValue("en", out AppTemplateText? en) ? en : v.Languages.Values.First();
        foreach (string part in new[] { text.Subject, text.Text, text.Html })
        {
            foreach (Match m in PlaceholderPattern.Matches(part))
            {
                if (!values.ContainsKey(m.Groups[1].Value))
                {
                    return (string.Empty, string.Empty, string.Empty, v.Version, "template_value_missing", m.Groups[1].Value);
                }
            }
        }
        string subject = PlaceholderPattern.Replace(text.Subject, m => values[m.Groups[1].Value].Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
        string plain = PlaceholderPattern.Replace(text.Text, m => values[m.Groups[1].Value]);
        string html = PlaceholderPattern.Replace(text.Html, m => WebUtility.HtmlEncode(values[m.Groups[1].Value]));
        return (subject, plain, html, v.Version, null, string.Empty);
    }

    /// <summary>The placeholders a template's English version uses, for the console.</summary>
    /// <param name="template">The template.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> AppTemplatePlaceholders(AppTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Current?.Languages.GetValueOrDefault("en") is not AppTemplateText en)
        {
            return Array.Empty<string>();
        }
        return PlaceholderPattern.Matches(en.Subject + " " + en.Text + " " + en.Html).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Mark a language of a template checked by a person.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="name">The template.</param>
    /// <param name="language">The language.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> CheckAppTemplateLanguageAsync(Guid tenantId, string name, string language, CancellationToken ct = default)
    {
        List<AppTemplate> all = await this.AppTemplatesAsync(tenantId, ct).ConfigureAwait(false);
        if (all.Find(t => t.Name == name)?.Current?.Languages.GetValueOrDefault(language) is not AppTemplateText text)
        {
            return false;
        }
        text.Checked = true;
        await this.WriteTenantDocumentAsync(tenantId, AppTemplatesKind, all, ct).ConfigureAwait(false);
        return true;
    }
}
