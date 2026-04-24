using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Runner;

namespace YobaConf.Tests.Runner;

public sealed class RunnerTests : IDisposable
{
	readonly WebApplicationFactory<Program> _factory;
	readonly string _tmpDir;
	readonly HttpClient _http;

	public RunnerTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-runner-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Storage:DataDirectory"] = _tmpDir,
			}));
		});
		// The WebApplicationFactory client points at the TestServer — no real TCP port —
		// which is what Runner uses for its resolves.
		_http = _factory.CreateClient();
	}

	public void Dispose()
	{
		_http.Dispose();
		_factory.Dispose();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	sealed class CapturingExec : IChildExec
	{
		public IReadOnlyDictionary<string, string> LastEnv { get; private set; } = new Dictionary<string, string>();
		public IReadOnlyList<string> LastArgs { get; private set; } = [];
		public int ExitCodeToReturn { get; set; }

		public Task<int> RunAsync(IReadOnlyDictionary<string, string> env, IReadOnlyList<string> childArgs, CancellationToken ct)
		{
			LastEnv = env;
			LastArgs = childArgs;
			return Task.FromResult(ExitCodeToReturn);
		}
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
		Id = 0,
		TagSet = t,
		KeyPath = k,
		Kind = BindingKind.Plain,
		ValuePlain = v,
		ContentHash = string.Empty,
		UpdatedAt = DateTimeOffset.UnixEpoch,
	};

	[Fact]
	public async Task HappyFlow_Applies_Env_And_Returns_Child_ExitCode()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"prod-db\""));
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.port", "5432"));
		var apiKey = keys.Create(TagSet.From([new("env", "prod")]), null, "runner", DateTimeOffset.UtcNow);

		var exec = new CapturingExec { ExitCodeToReturn = 7 };
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: apiKey.Plaintext,
			Tags: new Dictionary<string, string> { ["env"] = "prod" },
			Template: "envvar",
			ChildArgs: ["mycmd", "--arg"]);
		var code = await runner.RunAsync(opts, CancellationToken.None);

		code.Should().Be(7, "runner mirrors child exit code");
		exec.LastEnv.Should().ContainKey("DB_HOST").WhoseValue.Should().Be("prod-db");
		exec.LastEnv.Should().ContainKey("DB_PORT").WhoseValue.Should().Be("5432");
		exec.LastArgs.Should().Equal(["mycmd", "--arg"]);
	}

	[Fact]
	public async Task Conflict_Returns_ExitCode_Two_And_Writes_Diagnostic()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""));
		bindings.Upsert(Plain(TagSet.From([new("project", "y")]), "log-level", "\"Debug\""));
		var apiKey = keys.Create(TagSet.Empty, null, "runner", DateTimeOffset.UtcNow);

		var exec = new CapturingExec();
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: apiKey.Plaintext,
			Tags: new Dictionary<string, string> { ["env"] = "prod", ["project"] = "y" },
			Template: "envvar",
			ChildArgs: ["cmd"]);
		var code = await runner.RunAsync(opts, CancellationToken.None);

		code.Should().Be(ExitCodes.Conflict);
		stderr.ToString().Should().Contain("409 conflict");
		exec.LastArgs.Should().BeEmpty("child must not have been spawned on conflict");
	}

	[Fact]
	public async Task ScopeMismatch_Returns_ExitCode_Three()
	{
		var (_, keys) = Admin();
		// Key requires env=prod; request supplies env=staging → 403.
		var apiKey = keys.Create(TagSet.From([new("env", "prod")]), null, "prod-only", DateTimeOffset.UtcNow);

		var exec = new CapturingExec();
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: apiKey.Plaintext,
			Tags: new Dictionary<string, string> { ["env"] = "staging" },
			Template: "envvar",
			ChildArgs: ["cmd"]);
		(await runner.RunAsync(opts, CancellationToken.None)).Should().Be(ExitCodes.ScopeMismatch);
	}

	[Fact]
	public async Task Unauthorized_ApiKey_Returns_ConnectionError()
	{
		var exec = new CapturingExec();
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: "definitely-not-a-real-token",
			Tags: new Dictionary<string, string>(),
			Template: "envvar",
			ChildArgs: ["cmd"]);
		(await runner.RunAsync(opts, CancellationToken.None)).Should().Be(ExitCodes.ConnectionError);
	}

	[Fact]
	public async Task ConnectionError_To_Unreachable_Endpoint_Returns_Four()
	{
		var exec = new CapturingExec();
		var stderr = new StringWriter();
		using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
		var runner = new YobaConf.Runner.Runner(http, exec, stderr);

		// RFC-reserved unroutable IP — connect will hang then time out.
		var opts = new RunnerOptions(
			Endpoint: "http://192.0.2.1:59999",
			ApiKey: "irrelevant",
			Tags: new Dictionary<string, string>(),
			Template: "envvar",
			ChildArgs: ["cmd"]);
		(await runner.RunAsync(opts, CancellationToken.None)).Should().Be(ExitCodes.ConnectionError);
	}

	[Fact]
	public async Task Template_Param_Is_Forwarded_To_Server()
	{
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
		var apiKey = keys.Create(TagSet.Empty, null, "runner", DateTimeOffset.UtcNow);

		var exec = new CapturingExec();
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: apiKey.Plaintext,
			Tags: new Dictionary<string, string>(),
			Template: "dotnet",
			ChildArgs: ["cmd"]);
		await runner.RunAsync(opts, CancellationToken.None);

		// `dotnet` template double-underscores dots. Verifies the flag reached the server
		// and the runner parsed the flat response into env vars correctly.
		exec.LastEnv.Should().ContainKey("db__host").WhoseValue.Should().Be("x");
	}

	[Fact]
	public async Task FlatTemplate_Is_Rejected_As_NonUsable()
	{
		// Default Flat produces nested JSON — not usable as env vars. Runner detects and
		// surfaces a connection error rather than handing the child a pile of raw JSON.
		var (bindings, keys) = Admin();
		bindings.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
		var apiKey = keys.Create(TagSet.Empty, null, "runner", DateTimeOffset.UtcNow);

		var exec = new CapturingExec();
		var stderr = new StringWriter();
		var runner = new YobaConf.Runner.Runner(_http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: _http.BaseAddress!.ToString().TrimEnd('/'),
			ApiKey: apiKey.Plaintext,
			Tags: new Dictionary<string, string>(),
			Template: "flat",
			ChildArgs: ["cmd"]);
		var code = await runner.RunAsync(opts, CancellationToken.None);
		code.Should().Be(ExitCodes.ConnectionError);
		stderr.ToString().Should().Contain("non-flat JSON");
	}

	[Fact]
	public async Task ChildExec_NotCalled_OnFetchFailure()
	{
		var exec = new CapturingExec();
		var stderr = new StringWriter();
		using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
		var runner = new YobaConf.Runner.Runner(http, exec, stderr);

		var opts = new RunnerOptions(
			Endpoint: "http://192.0.2.1:59999",
			ApiKey: "k",
			Tags: new Dictionary<string, string>(),
			Template: "envvar",
			ChildArgs: ["cmd"]);
		await runner.RunAsync(opts, CancellationToken.None);
		exec.LastArgs.Should().BeEmpty();
	}
}
