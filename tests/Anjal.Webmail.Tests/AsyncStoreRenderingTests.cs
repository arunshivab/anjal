using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>Both store interfaces, so one proxy can stand in for the whole store.</summary>
public interface IFullStore : IMessageStore, IMailboxStore
{
}

/// <summary>
/// Wraps the in-memory store so that every call completes asynchronously,
/// the way PostgreSQL does. The in-memory store answers synchronously, so a
/// page that renders before its data has loaded never shows the problem in
/// ordinary tests; with this wrapper it does. That is how DEF-001 (the
/// dashboard's HTTP 500 on PostgreSQL) slipped past 677 tests.
/// </summary>
public class AsyncStoreProxy : DispatchProxy
{
    private object inner = null!;

    public static IFullStore Wrap(InMemoryMessageStore store)
    {
        IFullStore proxy = Create<IFullStore, AsyncStoreProxy>();
        ((AsyncStoreProxy)(object)proxy).inner = store;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Type returns = targetMethod.ReturnType;
        if (returns == typeof(Task))
        {
            return Later(() => (Task)targetMethod.Invoke(this.inner, args)!);
        }
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
        {
            MethodInfo later = typeof(AsyncStoreProxy).GetMethod(nameof(LaterOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(returns.GetGenericArguments()[0]);
            Func<object> call = () => targetMethod.Invoke(this.inner, args)!;
            return later.Invoke(null, new object[] { call });
        }
        return targetMethod.Invoke(this.inner, args);
    }

    private static async Task Later(Func<Task> call)
    {
        await Task.Delay(1);
        await call();
    }

    private static async Task<T> LaterOf<T>(Func<object> call)
    {
        await Task.Delay(1);
        return await (Task<T>)call();
    }
}

public sealed class AsyncStoreRenderingTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-async-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore inner = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.inner.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.inner.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.inner.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        await new MailboxSink(this.inner, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = Arun,
            RawBytes = System.Text.Encoding.ASCII.GetBytes("From: s@x.test\r\nSubject: Seeded\r\n\r\nbody\r\n"),
        });

        this.app = Program.CreateApp(Array.Empty<string>(), AsyncStoreProxy.Wrap(this.inner), this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };

        string html = await (await this.client.GetAsync("sign-in")).Content.ReadAsStringAsync();
        string token = TokenRegex.Match(html).Groups[1].Value;
        HttpResponseMessage login = await this.client.PostAsync("auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun@anjal.co.in",
            ["password"] = "correct horse battery",
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<string> MessagePathAsync()
    {
        string html = await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync();
        return Regex.Match(html, "/message/[0-9a-f-]{36}", RegexOptions.CultureInvariant).Value;
    }

    [Theory]
    [InlineData("folder/INBOX")]
    [InlineData("folder/Sent")]
    [InlineData("folder/Drafts")]
    [InlineData("folder/Junk")]
    [InlineData("folder/Trash")]
    [InlineData("compose")]
    [InlineData("settings")]
    [InlineData("search?q=Seeded")]
    [InlineData("dashboard")]
    [InlineData("dashboard?period=7d")]
    [InlineData("dashboard?period=30d")]
    [InlineData("dashboard?period=90d")]
    [InlineData("dashboard?period=365d")]
    public async Task EveryPage_RendersWhenTheStoreIsAsynchronous(string path)
    {
        HttpResponseMessage res = await this.client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task MessageAndReplyPages_RenderWhenTheStoreIsAsynchronous()
    {
        string message = await this.MessagePathAsync();
        Assert.NotEmpty(message);
        string id = message.Substring("/message/".Length);
        foreach (string path in new[] { message.TrimStart('/'), $"compose?reply={id}", $"compose?replyall={id}", $"compose?forward={id}" })
        {
            HttpResponseMessage res = await this.client.GetAsync(path);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)res.StatusCode}");
        }
    }

    [Theory]
    [InlineData("folder/INBOX?page=abc")]
    [InlineData("folder/INBOX?page=-5")]
    [InlineData("folder/INBOX?page=99999999999")]
    [InlineData("search?q=Seeded&page=abc")]
    [InlineData("compose?reply=not-a-guid")]
    [InlineData("compose?replyall=123")]
    [InlineData("compose?forward=xyz")]
    [InlineData("compose?forward=00000000-0000-0000-0000-000000000000")]
    public async Task MalformedOrUnknownUrlParameters_AreIgnored_NotAnError(string path)
    {
        // DEF-005: a hand-edited or truncated link used to throw while binding
        // the parameter. It now falls back: page 1, or a blank compose.
        HttpResponseMessage res = await this.client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
