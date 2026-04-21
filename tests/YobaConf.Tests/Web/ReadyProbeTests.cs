using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Web;

public sealed class ReadyProbeTests : IClassFixture<WebApplicationFactory<Program>>
{
	readonly WebApplicationFactory<Program> _factory;

	public ReadyProbeTests(WebApplicationFactory<Program> factory) => _factory = factory;

	HttpClient MakeClient(IConfigStore store) =>
		_factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IConfigStore>();
				services.AddSingleton(store);
				services.RemoveAll<IApiKeyStore>();
				services.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore([]));
			});
		}).CreateClient();

	[Fact]
	public async Task Ready_WithHealthyStore_Returns200()
	{
		using var client = MakeClient(new InMemoryConfigStore());

		using var resp = await client.GetAsync(new Uri("/ready", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("\"ready\"");
	}

	[Fact]
	public async Task Ready_WithThrowingStore_Returns503_WithErrorMessage()
	{
		using var client = MakeClient(new ThrowingConfigStore("db closed for maintenance"));

		using var resp = await client.GetAsync(new Uri("/ready", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("not ready");
		body.Should().Contain("db closed for maintenance");
	}

	[Fact]
	public async Task Ready_IsAnonymous_DoesNotRequireAuth()
	{
		using var client = MakeClient(new InMemoryConfigStore());

		using var resp = await client.GetAsync(new Uri("/ready", UriKind.Relative));

		// No 302 to /Login — the endpoint bypasses cookie-auth via AllowAnonymous.
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
