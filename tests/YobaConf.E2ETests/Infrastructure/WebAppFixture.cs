using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Tags;

namespace YobaConf.E2ETests.Infrastructure;

// Shared Kestrel+Playwright fixture. A single cookie is seeded via a one-time login so every
// test context can skip the login roundtrip unless it's testing login itself.
public sealed class WebAppFixture : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "test-pass";

    readonly KestrelAppHost _host = new();
    IPlaywright? _playwright;
    IBrowser? _browser;

    public string BaseUrl => _host.BaseUrl;
    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
    public IServiceProvider Services => _host.Services;

    public IBindingStore BindingStore => Services.GetRequiredService<IBindingStore>();
    public IBindingStoreAdmin BindingAdmin => Services.GetRequiredService<IBindingStoreAdmin>();
    public IApiKeyAdmin ApiKeyAdmin => Services.GetRequiredService<IApiKeyAdmin>();
    public IUserAdmin UserAdmin => Services.GetRequiredService<IUserAdmin>();
    public IUserStore UserStore => Services.GetRequiredService<IUserStore>();
    public ITagVocabularyStore VocabularyStore => Services.GetRequiredService<ITagVocabularyStore>();
    public ITagVocabularyAdmin VocabularyAdmin => Services.GetRequiredService<ITagVocabularyAdmin>();

    string _storageStatePath = "";

    public async Task InitializeAsync()
    {
        // Config-admin bootstrap: the seed-login below runs against the DB-empty state so the
        // config path is honored. Once tests start creating DB users, subsequent logins route
        // through the DB (config path remains for recovery). Password hash generated at
        // startup to keep the fixture self-contained.
        var hash = AdminPasswordHasher.Hash(AdminPassword);
        await _host.StartAsync(s =>
        {
            s["Admin:Username"] = AdminUsername;
            s["Admin:PasswordHash"] = hash;
            // 32 bytes of 0x42 base64 — shared with unit tests, lets secret bindings
            // encrypt/decrypt in fixture-seeded scenarios.
            s["YOBACONF_MASTER_KEY"] = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=";
        });

        // Warm-up: force DI singletons (SqliteBindingStore / SqliteApiKeyStore / SqliteUserStore)
        // to construct once in the main thread before concurrent requests race through DI
        // resolution and schema-replay on the same .db file. Without this the seed-login has
        // flaked in ~10% of runs because the Login POST and the cookie-auth validation race
        // each other's first-touch of DI.
        _ = Services.GetService<Core.Bindings.IBindingStore>();
        _ = Services.GetService<Core.Auth.IApiKeyStore>();
        _ = Services.GetService<Core.Auth.IUserStore>();

        // Block until Kestrel actually answers — StartAsync returns as soon as the listener
        // binds, but the first few requests can arrive before RazorPages + DataProtection
        // finish warming. Polling /health is the cheapest signal that the pipeline is alive.
        using (var warmupClient = new HttpClient { BaseAddress = new Uri(BaseUrl) })
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    var res = await warmupClient.GetAsync(new Uri("/health", UriKind.Relative));
                    if (res.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { /* keep polling */ }
                await Task.Delay(100);
            }
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // One-time authenticated-context capture. Every test (except LoginTests) loads this
        // storage-state and is pre-authenticated.
        _storageStatePath = Path.Combine(
            Path.GetTempPath(),
            "yobaconf-ui-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await using var seedCtx = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        // Same CDN stub as test contexts — the post-login landing page renders _Layout which
        // pulls htmx from unpkg, and without the stub headless Chromium stalls on the CDN.
        await seedCtx.RouteAsync("**/unpkg.com/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/javascript",
            Body = "/* stubbed CDN fetch */",
        }));

        var seedPage = await seedCtx.NewPageAsync();
        await seedPage.GotoAsync("/Login");
        await seedPage.GetByTestId("login-username").FillAsync(AdminUsername);
        await seedPage.GetByTestId("login-password").FillAsync(AdminPassword);
        await seedPage.GetByTestId("login-submit").ClickAsync();
        // After login we should land on / where the Phase-A placeholder renders.
        await Expect(seedPage.GetByTestId("index-phase-a-placeholder")).ToBeVisibleAsync();
        await seedCtx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _storageStatePath });
    }

    public Task<IBrowserContext> NewContextAsync(bool authenticated = true) =>
        NewContextAsync(authenticated, trace: true);

    public async Task<IBrowserContext> NewContextAsync(bool authenticated, bool trace)
    {
        var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            StorageStatePath = authenticated && !string.IsNullOrEmpty(_storageStatePath) ? _storageStatePath : null,
        });

        // _Layout.cshtml pulls htmx + prism from unpkg — blocking fetches. Headless Chromium
        // stalls 15-30s trying the CDN. Stub with an empty 200 so the DOM parser unblocks; no
        // test exercises htmx-driven flows yet, so stubbing is cheap + safe.
        await ctx.RouteAsync("**/unpkg.com/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/javascript",
            Body = "/* stubbed CDN fetch */",
        }));

        if (trace)
            await TraceArtifact.StartAsync(ctx);
        return ctx;
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
        await _host.DisposeAsync();
        if (!string.IsNullOrEmpty(_storageStatePath) && File.Exists(_storageStatePath))
            File.Delete(_storageStatePath);
    }
}
