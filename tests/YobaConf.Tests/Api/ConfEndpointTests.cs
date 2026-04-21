using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Api;

// Integration tests for `GET /v1/conf/{path}`. Use WebApplicationFactory<Program> to spin
// up the whole YobaConfApp pipeline, then swap IConfigStore and IApiKeyStore for in-memory
// fakes — the default SqliteConfigStore would try to open a `.db` file.
//
// HTTPS redirection is no longer wired into the app (Caddy terminates TLS at the edge
// in prod), so the factory's default http:// HttpClient works without special env flags.
public sealed class ConfEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	readonly WebApplicationFactory<Program> factory;

	public ConfEndpointTests(WebApplicationFactory<Program> factory) => this.factory = factory;

	sealed class Fixture
	{
		public InMemoryConfigStore Store { get; set; } = new InMemoryConfigStore();
		public List<(string Token, NodePath Root, string Desc)> Keys { get; } = [];
	}

	HttpClient MakeClient(Action<Fixture> seed)
	{
		var fx = new Fixture();
		seed(fx);

		var client = factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureServices(services =>
			{
				// Remove defaults registered by YobaConfApp.ConfigureServices.
				services.RemoveAll<IConfigStore>();
				services.RemoveAll<IApiKeyStore>();

				services.AddSingleton<IConfigStore>(fx.Store);
				services.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore(fx.Keys));
			});
		}).CreateClient();

		return client;
	}

	static HoconNode Node(string path, string content) =>
		new(NodePath.ParseDb(path), content, DateTimeOffset.UnixEpoch);

	static InMemoryConfigStore StoreWith(params (string path, string content)[] nodes) =>
		new(nodes: nodes.ToDictionary(n => NodePath.ParseDb(n.path), n => Node(n.path, n.content)));

	[Fact]
	public async Task MissingApiKey_Returns401()
	{
		using var client = MakeClient(fx => fx.Store.GetType());

		using var resp = await client.GetAsync(new Uri("/v1/conf/app", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task InvalidApiKey_Returns401()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid-token", NodePath.Root, "master"));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "wrong-token");

		using var resp = await client.GetAsync(new Uri("/v1/conf/app", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task OutOfScopePath_Returns403_BeforeNodeLookup()
	{
		// Scope = projects/yoba; request = projects/other. Must return 403 without revealing
		// whether projects/other exists. We don't seed the node so existence is moot; the
		// invariant tested is that 403 comes from scope check, not from 404 lookup.
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.ParseDb("projects/yoba"), "scoped"));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/projects.other", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task ScopeBoundary_AppVsApplication_Not_LeakedByPrefix()
	{
		// Key scoped to `yobaproj/yobaapp`; request `yobaproj/yobaapplication` must 403
		// (NodePath.IsAncestorOf handles the segment boundary check).
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.ParseDb("yobaproj/yobaapp"), "scoped"));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/yobaproj.yobaapplication", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task MissingNode_WithInScopeKey_Returns404()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/nowhere", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task ExistingNode_Returns200_WithJson_AndETagHeader()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			var nodes = new Dictionary<NodePath, HoconNode>
			{
				[NodePath.ParseDb("app")] = Node("app", "name = yoba\nport = 8080"),
			};
			fx.Store.GetType(); // keep var in closure
			ReplaceStore(fx, new InMemoryConfigStore(nodes: nodes));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/app", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Be("""{"name":"yoba","port":8080}""");
		resp.Headers.ETag.Should().NotBeNull();
		resp.Headers.ETag!.Tag.Should().MatchRegex("^\"[0-9a-f]{16}\"$");
	}

	[Fact]
	public async Task MatchingIfNoneMatch_Returns304_WithEtagHeader_AndEmptyBody()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			ReplaceStore(fx, StoreWith(("app", "x = 1")));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		// First call to capture the ETag
		using var first = await client.GetAsync(new Uri("/v1/conf/app", UriKind.Relative));
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		var etag = first.Headers.ETag!.Tag;

		// Second call with If-None-Match
		using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/conf/app", UriKind.Relative));
		req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
		using var second = await client.SendAsync(req);

		second.StatusCode.Should().Be(HttpStatusCode.NotModified);
		second.Headers.ETag!.Tag.Should().Be(etag);
		var body = await second.Content.ReadAsStringAsync();
		body.Should().BeEmpty();
	}

	[Fact]
	public async Task StaleIfNoneMatch_Returns200_WithFreshEtag()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			ReplaceStore(fx, StoreWith(("app", "x = 1")));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/conf/app", UriKind.Relative));
		req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"deadbeefdeadbeef\""));
		using var resp = await client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		resp.Headers.ETag!.Tag.Should().NotBe("\"deadbeefdeadbeef\"");
	}

	[Fact]
	public async Task DotSeparated_Url_ParsesAsNestedPath()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			ReplaceStore(fx, StoreWith(("yobaproj/yobaapp/prod", "env = production")));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/yobaproj.yobaapp.prod", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Be("""{"env":"production"}""");
	}

	[Fact]
	public async Task InvalidSlug_Returns400()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		// `Uppercase` segment violates slug regex (must start lowercase).
		using var resp = await client.GetAsync(new Uri("/v1/conf/Uppercase", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task QueryStringApiKey_Works_LikeHeader()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			ReplaceStore(fx, StoreWith(("app", "x = 1")));
		});

		using var resp = await client.GetAsync(new Uri("/v1/conf/app?apiKey=valid", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Fallthrough_Serves_NearestAncestor()
	{
		using var client = MakeClient(fx =>
		{
			fx.Keys.Add(("valid", NodePath.Root, "master"));
			ReplaceStore(fx, StoreWith(("app", "fallback = \"yes\"")));
		});
		client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "valid");

		using var resp = await client.GetAsync(new Uri("/v1/conf/app.missing.feature", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Be("""{"fallback":"yes"}""");
	}

	static void ReplaceStore(Fixture fx, InMemoryConfigStore replacement) => fx.Store = replacement;
}
