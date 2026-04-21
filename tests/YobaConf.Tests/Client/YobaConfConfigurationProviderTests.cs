using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YobaConf.Client;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Client;

// Full vertical E2E: spin up yobaconf via WebApplicationFactory, seed nodes + API keys
// into in-memory fakes, build IConfiguration through YobaConf.Client's `AddYobaConf`
// extension, assert IConfiguration reads return the expected values.
//
// The SDK gets an HttpMessageHandler from the TestServer so it talks to the in-process
// yobaconf instance — no sockets, no threading surprises from a real HTTP pipeline.
public sealed class YobaConfConfigurationProviderTests : IClassFixture<WebApplicationFactory<Program>>
{
	const string Token = "test-api-key";

	readonly WebApplicationFactory<Program> _factory;

	public YobaConfConfigurationProviderTests(WebApplicationFactory<Program> factory) =>
		_factory = factory;

	WebApplicationFactory<Program> MakeServer(Action<InMemoryConfigStore> seedStore) =>
		_factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
			var store = new InMemoryConfigStore();
			seedStore(store);
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IConfigStore>();
				services.RemoveAll<IConfigStoreAdmin>();
				services.RemoveAll<IApiKeyStore>();
				services.AddSingleton<IConfigStore>(store);
				services.AddSingleton<IConfigStoreAdmin>(store);
				services.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore(
					[(Token, NodePath.Root, "test")]));
			});
		});

	static IConfiguration BuildClient(WebApplicationFactory<Program> server, string path, bool optional = false, TimeSpan? refresh = null) =>
		new ConfigurationBuilder()
			.AddYobaConf(o =>
			{
				o.BaseUrl = "http://localhost";
				o.ApiKey = Token;
				o.Path = path;
				o.Optional = optional;
				o.RefreshInterval = refresh ?? TimeSpan.Zero; // disable polling in tests
				o.Handler = server.Server.CreateHandler();
			})
			.Build();

	[Fact]
	public void Basic_Load_ReadsKeysFromYobaconf()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "name = yoba\nport = 8080", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");

		config["name"].Should().Be("yoba");
		config.GetValue<int>("port").Should().Be(8080);
	}

	[Fact]
	public void NestedKeys_MapToColonPath()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "db { host = localhost\nport = 5432 }", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");

		config["db:host"].Should().Be("localhost");
		config.GetValue<int>("db:port").Should().Be(5432);
	}

	[Fact]
	public void HoconSubstitution_ResolvedBeforeFlattening()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "db_host = prod-db\nconnection = ${db_host}", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");

		config["connection"].Should().Be("prod-db");
	}

	[Fact]
	public void Variables_InheritedFromAncestor_VisibleToClient()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("project-a/prod"), "db = ${db_host}", DateTimeOffset.UnixEpoch);
			store.UpsertVariable(NodePath.ParseDb("project-a"), "db_host", "parent-db", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "project-a.prod");

		config["db"].Should().Be("parent-db");
	}

	[Fact]
	public void MissingApiKey_NonOptional_ThrowsOnBuild()
	{
		using var server = MakeServer(_ => { });

		var act = () => new ConfigurationBuilder()
			.AddYobaConf(o =>
			{
				o.BaseUrl = "http://localhost";
				o.ApiKey = "wrong-key";
				o.Path = "app";
				o.RefreshInterval = TimeSpan.Zero;
				o.Handler = server.Server.CreateHandler();
			})
			.Build();

		act.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void MissingNode_Optional_StartsEmpty_NoThrow()
	{
		using var server = MakeServer(_ => { });

		var config = BuildClient(server, "nowhere", optional: true);

		config["anything"].Should().BeNull();
	}

	[Fact]
	public void MissingNode_NonOptional_ThrowsOnBuild()
	{
		using var server = MakeServer(_ => { });

		var act = () => BuildClient(server, "nowhere", optional: false);

		act.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void Fallthrough_Serves_Ancestor_ToClient()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "fallback = \"parent-value\"", DateTimeOffset.UnixEpoch);
		});

		// Request a descendant that doesn't exist — yobaconf fallthroughs to `app`.
		var config = BuildClient(server, "app.missing.feature");

		config["fallback"].Should().Be("parent-value");
	}

	[Fact]
	public void ArrayValues_FlattenToIndexedKeys()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "servers = [alpha, beta, gamma]", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");

		config["servers:0"].Should().Be("alpha");
		config["servers:1"].Should().Be("beta");
		config["servers:2"].Should().Be("gamma");
	}

	[Fact]
	public void GetSection_Binds_To_Nested_Object()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "db { host = localhost\nport = 5432 }", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");
		var db = config.GetSection("db");

		db["host"].Should().Be("localhost");
		db.GetValue<int>("port").Should().Be(5432);
	}

	[Fact]
	public void TypedBinding_Works_On_Nested_Section()
	{
		using var server = MakeServer(store =>
		{
			store.UpsertNode(NodePath.ParseDb("app"), "db { host = localhost\nport = 5432 }", DateTimeOffset.UnixEpoch);
		});

		var config = BuildClient(server, "app");
		var db = config.GetSection("db").Get<DbOptions>();

		db.Should().NotBeNull();
		db!.Host.Should().Be("localhost");
		db.Port.Should().Be(5432);
	}

	sealed class DbOptions
	{
		public string? Host { get; set; }
		public int Port { get; set; }
	}
}
