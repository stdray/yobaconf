using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using YobaConf.Core.Storage;

namespace YobaConf.E2ETests.Infrastructure;

// UI-flavoured host: shared Kestrel bootstrap + a Playwright Chromium instance, with one-
// time admin login whose cookies are persisted to a storage-state file and reused by every
// test context. Skips the per-test login round-trip and eliminates the cookie-auth race we
// saw without it.
//
// One-time browser install after a fresh clone (Cake `E2ETest` target runs this automatically):
//     pwsh bin/Debug/net10.0/playwright.ps1 install chromium
public sealed class WebAppFixture : IAsyncLifetime
{
	public const string AdminUsername = "admin";
	public const string AdminPassword = "test";

	// 32 bytes of 0x42 base64-encoded. Deterministic so test seed can hardcode encrypted
	// values without re-running the encryptor from the test body when that's convenient.
	public const string MasterKeyBase64 = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=";

	readonly KestrelAppHost _host = new();
	IPlaywright? _playwright;
	IBrowser? _browser;
	string _storageStatePath = "";

	public string BaseUrl => _host.BaseUrl;
	public IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
	public IConfigStoreAdmin ConfigStoreAdmin => _host.Services.GetRequiredService<IConfigStoreAdmin>();
	public IServiceProvider Services => _host.Services;

	public async Task InitializeAsync()
	{
		// Admin:PasswordHash via real PBKDF2 — mirrors prod; plaintext never reaches the app.
		var adminHash = AdminPasswordHasher.Hash(AdminPassword);

		await _host.StartAsync(s =>
		{
			s["Admin:Username"] = AdminUsername;
			s["Admin:PasswordHash"] = adminHash;
			// Master key so ISecretEncryptor registers — Secret-touching tests can seed
			// encrypted values directly via _host.Services without wiring their own encryptor.
			s["YOBACONF_MASTER_KEY"] = MasterKeyBase64;
			// API-key for /v1/conf tests that exercise the API surface alongside the UI.
			s["ApiKeys:Keys:0:Token"] = "e2e-test-key";
			s["ApiKeys:Keys:0:RootPath"] = "";
			s["ApiKeys:Keys:0:Description"] = "E2E test key (root scope)";
		});

		_playwright = await Playwright.CreateAsync();
		_browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
		{
			Headless = true,
		});

		// One-time login → persist cookies for reuse by every test context.
		_storageStatePath = Path.Combine(
			Path.GetTempPath(),
			"yobaconf-ui-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");

		await using var seedCtx = await NewContextAsync(authenticated: false, trace: false);
		var seedPage = await seedCtx.NewPageAsync();
		await seedPage.GotoAsync("/Login");
		await seedPage.GetByTestId("login-username").FillAsync(AdminUsername);
		await seedPage.GetByTestId("login-password").FillAsync(AdminPassword);
		await seedPage.GetByTestId("login-submit").ClickAsync();
		// `nav-configs` is in the authenticated `_Layout.cshtml` — its visibility proves
		// the post-login redirect landed on an auth-gated page with a valid cookie.
		// Element-based sentinel (mirrors yobalog's fixture pattern) is more robust than
		// a URL regex, which can hang if the redirect target hits an intermediate error
		// page that isn't /Login.
		await Expect(seedPage.GetByTestId("nav-configs")).ToBeVisibleAsync();
		await seedCtx.StorageStateAsync(new BrowserContextStorageStateOptions
		{
			Path = _storageStatePath,
		});
	}

	public Task<IBrowserContext> NewContextAsync(bool authenticated = true) =>
		NewContextAsync(authenticated, trace: true);

	// `trace: false` skips trace startup — used by the fixture's own seed-login context which
	// closes before any test class could consume the trace.
	public async Task<IBrowserContext> NewContextAsync(bool authenticated, bool trace)
	{
		var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions
		{
			BaseURL = BaseUrl,
			IgnoreHTTPSErrors = true,
			StorageStatePath = authenticated && !string.IsNullOrEmpty(_storageStatePath)
				? _storageStatePath
				: null,
		});
		// `_Layout.cshtml` pulls htmx + prism.js from unpkg.com with <script src="https://unpkg.com/...">.
		// Headless Chromium's CDN fetches stall 15-30s on some test runs and the page never hits
		// `load` state → GotoAsync times out. The scripts aren't exercised by any current E2E
		// test, so stub them with an empty 200. Mirrors yobalog's fixture pattern (Fixtures/htmx).
		// When a test needs real htmx behavior, copy the asset into Fixtures/ and switch that one
		// route to serve the file; the stub stays for everything else.
		await ctx.RouteAsync("**/unpkg.com/**", route => route.FulfillAsync(new RouteFulfillOptions
		{
			Status = 200,
			ContentType = route.Request.Url.EndsWith(".css", StringComparison.Ordinal)
				? "text/css"
				: "application/javascript",
			Body = "/* yobaconf E2E: external CDN stubbed */",
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
