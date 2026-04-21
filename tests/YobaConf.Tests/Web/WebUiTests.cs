using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Web;

// Integration tests for the Phase A UI: cookie-auth, tree listing, node detail, paste-import.
// Use WebApplicationFactory<Program> + in-memory store so we don't touch SQLite. Seed the
// admin credential into the configuration via `ConfigureAppConfiguration` so LoginModel's
// IOptions<AdminOptions> resolves to real values.
public sealed class WebUiTests : IClassFixture<WebApplicationFactory<Program>>
{
	const string AdminUser = "admin";
	const string AdminPassword = "test-password";

	readonly WebApplicationFactory<Program> _factory;

	public WebUiTests(WebApplicationFactory<Program> factory) => _factory = factory;

	sealed class Fixture
	{
		public InMemoryConfigStore Store { get; } = new();
	}

	HttpClient MakeClient(Action<Fixture>? seed = null, bool followRedirects = true)
	{
		var fx = new Fixture();
		seed?.Invoke(fx);

		var client = _factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureAppConfiguration((_, cfg) =>
			{
				// Admin credentials injected via in-memory config provider. PBKDF2 hash is
				// computed at fixture-setup time — tests use the same plaintext below.
				var hash = AdminPasswordHasher.Hash(AdminPassword);
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Admin:Username"] = AdminUser,
					["Admin:PasswordHash"] = hash,
				});
			});
			builder.ConfigureServices(services =>
			{
				// Swap SqliteConfigStore + ConfigApiKeyStore for in-memory fakes so tests
				// don't touch disk or require any API keys configured.
				services.RemoveAll<IConfigStore>();
				services.RemoveAll<IConfigStoreAdmin>();
				services.RemoveAll<IApiKeyStore>();
				services.AddSingleton<IConfigStore>(fx.Store);
				services.AddSingleton<IConfigStoreAdmin>(fx.Store);
				services.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore([]));
			});
		}).CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = followRedirects,
		});

		return client;
	}

	static async Task<HttpClient> Authenticate(HttpClient client)
	{
		var form = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("username", AdminUser),
			new KeyValuePair<string, string>("password", AdminPassword),
			new KeyValuePair<string, string>("returnUrl", "/"),
		});
		using var resp = await client.PostAsync(new Uri("/Login", UriKind.Relative), form);
		// AllowAutoRedirect = true by default; after 302 → / the client has the auth cookie.
		// Status should be 200 (Index) after redirect, or 302 if following disabled.
		return client;
	}

	[Fact]
	public async Task UnauthenticatedGet_Index_Redirects_ToLogin()
	{
		using var client = MakeClient(followRedirects: false);

		using var resp = await client.GetAsync(new Uri("/", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/Login");
	}

	[Fact]
	public async Task LoginPage_Loads_WithoutAuth()
	{
		using var client = MakeClient(followRedirects: false);

		using var resp = await client.GetAsync(new Uri("/Login", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("data-testid=\"login-submit\"");
	}

	[Fact]
	public async Task LoginPost_WithWrongPassword_ShowsError()
	{
		using var client = MakeClient();

		var form = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("username", AdminUser),
			new KeyValuePair<string, string>("password", "wrong"),
		});
		using var resp = await client.PostAsync(new Uri("/Login", UriKind.Relative), form);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("Invalid username or password");
	}

	[Fact]
	public async Task LoginPost_WithCorrectCredentials_SetsAuthCookie_AndRedirects()
	{
		using var client = MakeClient(followRedirects: false);

		var form = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("username", AdminUser),
			new KeyValuePair<string, string>("password", AdminPassword),
			new KeyValuePair<string, string>("returnUrl", "/"),
		});
		using var resp = await client.PostAsync(new Uri("/Login", UriKind.Relative), form);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Be("/");
		// Cookie set on the response
		resp.Headers.Should().ContainSingle(h => h.Key == "Set-Cookie")
			.Which.Value.Should().ContainSingle(v => v.Contains(".AspNetCore.Cookies", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AuthenticatedIndex_Empty_ShowsImportCta()
	{
		using var client = MakeClient();
		await Authenticate(client);

		using var resp = await client.GetAsync(new Uri("/", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("data-testid=\"import-empty-cta\"");
	}

	[Fact]
	public async Task AuthenticatedIndex_WithNodes_ListsThem()
	{
		using var client = MakeClient(fx =>
		{
			fx.Store.UpsertNode(NodePath.ParseDb("alpha"), "x = 1", DateTimeOffset.UnixEpoch);
			fx.Store.UpsertNode(NodePath.ParseDb("beta"), "y = 2", DateTimeOffset.UnixEpoch);
		});
		await Authenticate(client);

		using var resp = await client.GetAsync(new Uri("/", UriKind.Relative));

		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("data-path=\"alpha\"");
		body.Should().Contain("data-path=\"beta\"");
	}

	[Fact]
	public async Task Node_DetailView_ShowsRawContent_AndResolvedJson()
	{
		using var client = MakeClient(fx =>
		{
			fx.Store.UpsertNode(NodePath.ParseDb("app"), "name = yoba\nport = 8080", DateTimeOffset.UnixEpoch);
		});
		await Authenticate(client);

		using var resp = await client.GetAsync(new Uri("/Node?path=app", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		// Razor HTML-encodes content; testids are stable markers.
		body.Should().Contain("data-testid=\"node-raw\"");
		body.Should().Contain("name = yoba");
		body.Should().Contain("data-testid=\"node-json\"");
		// Port value inside HTML-encoded JSON — encoded as &quot;port&quot;.
		body.Should().Contain("port");
		body.Should().Contain("8080");
		body.Should().Contain("data-testid=\"node-etag\"");
	}

	[Fact]
	public async Task Node_MissingPath_FallsThrough_AndShowsNotice()
	{
		using var client = MakeClient(fx =>
		{
			fx.Store.UpsertNode(NodePath.ParseDb("app"), "fallback = \"parent\"", DateTimeOffset.UnixEpoch);
		});
		await Authenticate(client);

		using var resp = await client.GetAsync(new Uri("/Node?path=app.missing", UriKind.Relative));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("data-testid=\"node-fallthrough-notice\"");
		body.Should().Contain("data-testid=\"fallthrough-target\"");
	}

	[Fact]
	public async Task Import_Preview_ConvertsJson_ButDoesNotSave()
	{
		var fx = new Fixture();
		var client = _factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Admin:Username"] = AdminUser,
				["Admin:PasswordHash"] = AdminPasswordHasher.Hash(AdminPassword),
			}));
			builder.ConfigureServices(s =>
			{
				s.RemoveAll<IConfigStore>();
				s.RemoveAll<IConfigStoreAdmin>();
				s.RemoveAll<IApiKeyStore>();
				s.AddSingleton<IConfigStore>(fx.Store);
				s.AddSingleton<IConfigStoreAdmin>(fx.Store);
				s.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore([]));
			});
		}).CreateClient();

		await Authenticate(client);

		var form = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("TargetPath", "new-node"),
			new KeyValuePair<string, string>("Format", "json"),
			new KeyValuePair<string, string>("Source", """{"name":"yoba"}"""),
			new KeyValuePair<string, string>("Action", "preview"),
		});
		using var resp = await client.PostAsync(new Uri("/Import", UriKind.Relative), form);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		// Preview only — store untouched.
		fx.Store.ListNodePaths().Should().BeEmpty();
	}

	[Fact]
	public async Task Import_Save_CreatesNode_InStore()
	{
		var fx = new Fixture();
		var client = _factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Admin:Username"] = AdminUser,
				["Admin:PasswordHash"] = AdminPasswordHasher.Hash(AdminPassword),
			}));
			builder.ConfigureServices(s =>
			{
				s.RemoveAll<IConfigStore>();
				s.RemoveAll<IConfigStoreAdmin>();
				s.RemoveAll<IApiKeyStore>();
				s.AddSingleton<IConfigStore>(fx.Store);
				s.AddSingleton<IConfigStoreAdmin>(fx.Store);
				s.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore([]));
			});
		}).CreateClient();

		await Authenticate(client);

		var form = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("TargetPath", "projects.yoba"),
			new KeyValuePair<string, string>("Format", "env"),
			new KeyValuePair<string, string>("Source", "DB_HOST=localhost\nPORT=5432"),
			new KeyValuePair<string, string>("Action", "save"),
		});
		using var resp = await client.PostAsync(new Uri("/Import", UriKind.Relative), form);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var savedNode = fx.Store.FindNode(NodePath.ParseDb("projects/yoba"));
		savedNode.Should().NotBeNull();
		savedNode!.RawContent.Should().Contain("DB_HOST");
		savedNode.RawContent.Should().Contain("localhost");
	}

	[Fact]
	public async Task Logout_ClearsCookie_AndRedirectsToLogin()
	{
		using var client = MakeClient(followRedirects: false);
		// Auth first (follow the redirect manually).
		var authForm = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>("username", AdminUser),
			new KeyValuePair<string, string>("password", AdminPassword),
			new KeyValuePair<string, string>("returnUrl", "/"),
		});
		using var login = await client.PostAsync(new Uri("/Login", UriKind.Relative), authForm);
		login.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var logout = await client.PostAsync(new Uri("/Logout", UriKind.Relative), new StringContent(string.Empty, Encoding.UTF8));

		logout.StatusCode.Should().Be(HttpStatusCode.Redirect);
		logout.Headers.Location!.ToString().Should().Contain("/Login");
	}
}
