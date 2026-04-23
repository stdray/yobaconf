using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace YobaConf.Tests.Endpoints;

// Smoke tests for /health — liveness probe that Docker healthcheck and Cake DockerSmoke hit.
// Keeps .AllowAnonymous() and MapMethods([GET, HEAD]) from regressing silently.
public sealed class HealthEndpointTests : IDisposable
{
	readonly WebApplicationFactory<Program> _factory;
	readonly string _tmpDir;

	public HealthEndpointTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-health-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Storage:DataDirectory"] = _tmpDir,
				["Storage:FileName"] = "yobaconf.db",
			}));
		});
	}

	public void Dispose()
	{
		_factory.Dispose();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
	}

	[Fact]
	public async Task GET_Health_Returns_200_With_StatusHealthy()
	{
		using var client = _factory.CreateClient();
		var res = await client.GetAsync(new Uri("/health", UriKind.Relative));

		res.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await res.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(body);
		doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
	}

	[Fact]
	public async Task GET_Health_Anonymous_Access_Works()
	{
		// A plain HttpClient from CreateClient() has no auth cookie/token —
		// if /health loses .AllowAnonymous(), this drops to 401.
		using var client = _factory.CreateClient();
		var res = await client.GetAsync(new Uri("/health", UriKind.Relative));
		res.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task HEAD_Health_Returns_200()
	{
		using var client = _factory.CreateClient();
		var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri("/health", UriKind.Relative)));
		res.StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
