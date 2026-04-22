using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Web;

namespace YobaConf.E2ETests.Infrastructure;

// Shared Kestrel + YobaConfApp host for E2E tests. WebApplicationFactory hard-codes TestServer
// (no real TCP port), which Playwright can't speak to. We boot Kestrel on port 0 (OS picks
// free) and expose the address.
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
			["Storage:DataDirectory"] = _tempDir,
			["Storage:FileName"] = "yobaconf.db",
		};
		configure(settings);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = "Testing",
			// Ensure Razor Pages discovers pages from YobaConf.Web — the default application
			// name is the test assembly, which has no pages; every request would 404 onto the
			// fallback-policy challenge and redirect-loop /Login.
			ApplicationName = typeof(YobaConfApp).Assembly.GetName().Name,
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
		catch { /* best-effort */ }
	}
}
