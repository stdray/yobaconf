using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Client;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Client;

// End-to-end integration — YobaConf.Client talking to a live yobaconf via
// WebApplicationFactory's in-process HttpMessageHandler. Exercises the actual /v1/conf
// pipeline + ETag caching behaviour, not mocks.
public sealed class YobaConfConfigurationProviderTests : IDisposable
{
	readonly WebApplicationFactory<Program> _factory;
	readonly string _tmpDir;

	public YobaConfConfigurationProviderTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-client-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Storage:DataDirectory"] = _tmpDir,
			}));
		});
	}

	public void Dispose()
	{
		_factory.Dispose();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	(IBindingStoreAdmin bindings, IApiKeyAdmin keys) Admin()
	{
		var scope = _factory.Services.CreateScope();
		return (
			scope.ServiceProvider.GetRequiredService<IBindingStoreAdmin>(),
			scope.ServiceProvider.GetRequiredService<IApiKeyAdmin>());
	}

	static Binding Plain(TagSet t, string k, string v) => new()
	{
		Id = 0, TagSet = t, KeyPath = k, Kind = BindingKind.Plain,
		ValuePlain = v, ContentHash = string.Empty, UpdatedAt = DateTimeOffset.UnixEpoch,
	};

	IConfigurationRoot BuildConfig(Action<YobaConfConfigurationOptions> configure)
	{
		var builder = new ConfigurationBuilder();
		builder.AddYobaConf(o =>
		{
			o.BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/');
			o.Handler = _factory.Server.CreateHandler();
			// Polling off by default — tests explicitly opt in when they want to observe
			// reload behaviour. Avoids background pollers racing assertions.
			o.RefreshInterval = TimeSpan.Zero;
			configure(o);
		});
		return builder.Build();
	}

	[Fact]
	public void HappyLoad_Populates_Flattened_Keys()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"prod-db\""));
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.port", "5432"));
		var key = keys.Create(TagSet.From([new("env", "prod")]), null, "runtime", DateTimeOffset.UtcNow);

		var config = BuildConfig(o =>
		{
			o.ApiKey = key.Plaintext;
			o.WithTag("env", "prod");
		});

		config["db:host"].Should().Be("prod-db");
		config["db:port"].Should().Be("5432");
	}

	[Fact]
	public void TypedBinding_Works_End_To_End()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.Empty, "log.level", "\"Info\""));
		bindings.Upsert(Plain(TagSet.Empty, "log.format", "\"json\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		var config = BuildConfig(o => o.ApiKey = key.Plaintext);
		var section = config.GetSection("log").Get<LogOptions>();

		section.Should().NotBeNull();
		section!.Level.Should().Be("Info");
		section.Format.Should().Be("json");
	}

	sealed class LogOptions
	{
		public string Level { get; set; } = "";
		public string Format { get; set; } = "";
	}

	[Fact]
	public void Tag_Filter_Narrows_Bindings_To_Subset()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"prod-db\""));
		bindings.Upsert(Plain(TagSet.From([new("env", "staging")]), "db.host", "\"staging-db\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		var prodConfig = BuildConfig(o =>
		{
			o.ApiKey = key.Plaintext;
			o.WithTag("env", "prod");
		});
		prodConfig["db:host"].Should().Be("prod-db");

		var stagingConfig = BuildConfig(o =>
		{
			o.ApiKey = key.Plaintext;
			o.WithTag("env", "staging");
		});
		stagingConfig["db:host"].Should().Be("staging-db");
	}

	[Fact]
	public void Missing_ApiKey_Throws_At_Build()
	{
		// ApiKey is required even in Optional mode — no sensible fail-soft for an invalid
		// ctor argument.
		var builder = new ConfigurationBuilder();
		builder.AddYobaConf(o =>
		{
			o.BaseUrl = "http://example.com";
			o.ApiKey = "";
		});
		FluentActions.Invoking(() => builder.Build()).Should().Throw<ArgumentException>();
	}

	[Fact]
	public void NonOptional_BadApiKey_Throws_At_Load()
	{
		var config = new ConfigurationBuilder();
		config.AddYobaConf(o =>
		{
			o.BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/');
			o.Handler = _factory.Server.CreateHandler();
			o.ApiKey = "absolutely-not-a-real-token";
			o.Optional = false;
			o.RefreshInterval = TimeSpan.Zero;
		});
		FluentActions.Invoking(() => config.Build())
			.Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void Optional_BadApiKey_StartsEmpty()
	{
		var config = new ConfigurationBuilder();
		config.AddYobaConf(o =>
		{
			o.BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/');
			o.Handler = _factory.Server.CreateHandler();
			o.ApiKey = "nope";
			o.Optional = true;
			o.RefreshInterval = TimeSpan.Zero;
		});
		var root = config.Build();
		root["any"].Should().BeNull("optional load swallows the 401 and starts with no data");
	}

	[Fact]
	public void Empty_TagVector_Resolves_Only_Root_Bindings()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.Empty, "app.name", "\"yoba\""));
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"x\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		var config = BuildConfig(o => o.ApiKey = key.Plaintext);
		config["app:name"].Should().Be("yoba");
		config["db:host"].Should().BeNull("no env=prod in request → env=prod bindings excluded");
	}

	[Fact]
	public void Conflict_Propagates_As_Load_Exception_When_NonOptional()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "x", "\"a\""));
		bindings.Upsert(Plain(TagSet.From([new("project", "y")]), "x", "\"b\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		var config = new ConfigurationBuilder();
		config.AddYobaConf(o =>
		{
			o.BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/');
			o.Handler = _factory.Server.CreateHandler();
			o.ApiKey = key.Plaintext;
			o.Optional = false;
			o.RefreshInterval = TimeSpan.Zero;
			o.WithTag("env", "prod").WithTag("project", "y");
		});
		FluentActions.Invoking(() => config.Build()).Should().Throw<HttpRequestException>();
	}

	[Fact]
	public void ETag_Short_Circuits_Poll_With_NotModified()
	{
		// Two back-to-back loads of the same provider: the second request sends
		// If-None-Match, server returns 304 NotModified, data remains unchanged. Exercised
		// by manually driving the provider's Load twice (the public polling path is
		// timer-driven and harder to trigger deterministically).
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.Empty, "k", "\"v\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		using var provider = new YobaConfConfigurationProvider(new YobaConfConfigurationOptions
		{
			BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/'),
			ApiKey = key.Plaintext,
			Handler = _factory.Server.CreateHandler(),
			RefreshInterval = TimeSpan.Zero,
		});
		provider.Load();
		provider.TryGet("k", out var firstValue).Should().BeTrue();
		firstValue.Should().Be("v");

		// Second invocation should hit the ETag path. We don't have a hook to observe 304
		// directly, but the data dict stays intact and the cached value is still reachable.
		provider.Load();
		provider.TryGet("k", out var secondValue).Should().BeTrue();
		secondValue.Should().Be("v");
	}

	[Fact]
	public void Provider_Picks_Up_Changes_On_Reload()
	{
		var (bindings, keys) = Admin();
		var id1 = bindings.Upsert(Plain(TagSet.Empty, "k", "\"v1\""));
		var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

		using var provider = new YobaConfConfigurationProvider(new YobaConfConfigurationOptions
		{
			BaseUrl = _factory.Server.BaseAddress.ToString().TrimEnd('/'),
			ApiKey = key.Plaintext,
			Handler = _factory.Server.CreateHandler(),
			RefreshInterval = TimeSpan.Zero,
		});
		provider.Load();
		provider.TryGet("k", out var v1).Should().BeTrue();
		v1.Should().Be("v1");

		// Mutate server-side → next Load picks it up. In production the timer would fire;
		// here we drive it manually.
		bindings.Upsert(Plain(TagSet.Empty, "k", "\"v2\""));
		provider.Load();
		provider.TryGet("k", out var v2).Should().BeTrue();
		v2.Should().Be("v2");
	}

	[Fact]
	public void WithTags_Bulk_Populates_TagVector()
	{
		var options = new YobaConfConfigurationOptions();
		options.WithTags(new Dictionary<string, string>
		{
			["env"] = "prod",
			["project"] = "yobapub",
			["region"] = "eu-west",
		});
		options.Tags.Should().HaveCount(3);
		options.Tags["env"].Should().Be("prod");
	}
}
