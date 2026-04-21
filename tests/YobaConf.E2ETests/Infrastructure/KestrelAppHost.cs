using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Web;

namespace YobaConf.E2ETests.Infrastructure;

// Shared Kestrel + YobaConfApp bootstrap for tests that need a real TCP port (browser-backed
// UI tests). WebApplicationFactory hard-codes TestServer, so a headless-Chromium client can't
// reach it — Playwright needs a real HTTP endpoint.
//
// No `EnvironmentName = "Testing"` here; production code is config-driven, no magic env-name
// checks (plan.md invariant). Tests exercise real prod code paths.
public sealed class KestrelAppHost : IAsyncDisposable
{
	WebApplication? _app;
	string _tempDir = "";

	public string BaseUrl { get; private set; } = "";
	public string DataDir => _tempDir;
	public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Host not started");

	public async Task StartAsync(Action<IDictionary<string, string?>> configure)
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobaconf-e2e-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		var settings = new Dictionary<string, string?>
		{
			["SqliteConfigStore:DataDirectory"] = _tempDir,
		};
		configure(settings);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			// ApplicationName = YobaConf.Web so Razor Pages discovery finds pages in the
			// web assembly. Default entry assembly here is YobaConf.E2ETests, which has
			// no pages → authz fallback policy challenges every request and redirect-loops
			// on /Login.
			ApplicationName = typeof(YobaConfApp).Assembly.GetName().Name,
			// Point WebRootPath at the web project's wwwroot (not the test bin) so static-
			// files middleware doesn't warn and Playwright traces render with real CSS.
			WebRootPath = WebProjectWwwroot(),
		});
		builder.Configuration.AddInMemoryCollection(settings);
		builder.WebHost.UseKestrel();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		YobaConfApp.ConfigureServices(builder);

		_app = builder.Build();
		YobaConfApp.Configure(_app);
		await _app.StartAsync();

		BaseUrl = _app.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not report an address");
	}

	// csproj injects AssemblyMetadataAttribute("YobaConfWebProjectDir", <abs path to src/YobaConf.Web>).
	// Fail loudly if missing rather than falling back to cwd — the failure mode otherwise is a
	// confusing StaticFileMiddleware warning instead of a test failure.
	static string WebProjectWwwroot()
	{
		var attr = typeof(KestrelAppHost).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "YobaConfWebProjectDir")
			?? throw new InvalidOperationException(
				"AssemblyMetadataAttribute('YobaConfWebProjectDir') missing — check YobaConf.E2ETests.csproj.");
		return Path.Combine(attr.Value!, "wwwroot");
	}

	public async ValueTask DisposeAsync()
	{
		if (_app is not null)
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}
}
