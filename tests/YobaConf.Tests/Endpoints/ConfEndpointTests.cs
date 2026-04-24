using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Endpoints;

// Integration tests against /v1/conf via TestServer. One WebApplicationFactory per test —
// each gets a fresh tmp DB — so per-test state doesn't leak across the shared process.
public sealed class ConfEndpointTests : IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _tmpDir;

    public ConfEndpointTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-int-" + Guid.NewGuid().ToString("N"));
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

    (IBindingStoreAdmin bindings, IApiKeyAdmin keys) Admin()
    {
        var scope = _factory.Services.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<IBindingStoreAdmin>(),
            scope.ServiceProvider.GetRequiredService<IApiKeyAdmin>());
    }

    static Binding Plain(TagSet t, string keyPath, string valueJson) => new()
    {
        Id = 0,
        TagSet = t,
        KeyPath = keyPath,
        Kind = BindingKind.Plain,
        ValuePlain = valueJson,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task HappyPath_Returns_200_With_Json_And_ETag()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"prod-db\""));
        var key = keys.Create(TagSet.From([new("env", "prod")]), null, "prod", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var res = await client.GetAsync(new Uri("/v1/conf?env=prod", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.ETag.Should().NotBeNull();

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Be("""{"db":{"host":"prod-db"}}""");
    }

    [Fact]
    public async Task IfNoneMatch_MatchingETag_Returns_304()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.Empty, "k", "\"v\""));
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var first = await client.GetAsync(new Uri("/v1/conf", UriKind.Relative));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag!.Tag;

        var second = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/conf", UriKind.Relative));
        second.Headers.TryAddWithoutValidation("X-YobaConf-ApiKey", key.Plaintext);
        second.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        var notModified = await client.SendAsync(second);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Conflict_Returns_409_With_Diagnostic()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""));
        bindings.Upsert(Plain(TagSet.From([new("project", "yobapub")]), "log-level", "\"Debug\""));
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var res = await client.GetAsync(new Uri("/v1/conf?env=prod&project=yobapub", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("conflict");
        doc.RootElement.GetProperty("key").GetString().Should().Be("log-level");
        doc.RootElement.GetProperty("tiedBindings").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("hint").GetString().Should().Contain("overlay");
    }

    [Fact]
    public async Task MissingApiKey_Returns_401()
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(new Uri("/v1/conf?env=prod", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongApiKey_Returns_401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", "absolutely-wrong-token-22x");
        var res = await client.GetAsync(new Uri("/v1/conf?env=prod", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScopeMismatch_Returns_403()
    {
        var (_, keys) = Admin();
        var key = keys.Create(TagSet.From([new("env", "prod")]), null, "prod-only", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        // Request tag-vector is env=staging → key's RequiredTags {env:prod} not a subset.
        var res = await client.GetAsync(new Uri("/v1/conf?env=staging", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InvalidTagSlug_Returns_400()
    {
        var (_, keys) = Admin();
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        // Uppercase tag-key violates slug regex.
        var res = await client.GetAsync(new Uri("/v1/conf?Env=prod", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AllowedKeyPrefixes_Filter_Response()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        bindings.Upsert(Plain(TagSet.Empty, "cache.ttl", "300"));
        bindings.Upsert(Plain(TagSet.Empty, "secret-thing", "\"nope\""));
        var key = keys.Create(TagSet.Empty, ["db.", "cache."], "scoped", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var res = await client.GetAsync(new Uri("/v1/conf", UriKind.Relative));
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Be("""{"cache":{"ttl":300},"db":{"host":"x"}}""");
    }

    // ---- Phase C.1: template response shapes ----

    [Fact]
    public async Task DotnetTemplate_ReturnsFlatDoubleUnderscoreJson()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.Empty, "db.host", "\"prod-db\""));
        bindings.Upsert(Plain(TagSet.Empty, "db.port", "5432"));
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var res = await client.GetAsync(new Uri("/v1/conf?template=dotnet", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Be("""{"db__host":"prod-db","db__port":5432}""");
    }

    [Fact]
    public async Task UnknownTemplate_Returns400()
    {
        var (_, keys) = Admin();
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var res = await client.GetAsync(new Uri("/v1/conf?template=unknown", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Unknown template");
    }

    [Fact]
    public async Task ETag_StableAcrossRequests_WithSameTemplate()
    {
        var (bindings, keys) = Admin();
        bindings.Upsert(Plain(TagSet.Empty, "k", "\"v\""));
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-ApiKey", key.Plaintext);

        var first = await client.GetAsync(new Uri("/v1/conf?template=envvar", UriKind.Relative));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag!.Tag;

        var second = await client.GetAsync(new Uri("/v1/conf?template=envvar", UriKind.Relative));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.ETag!.Tag.Should().Be(etag);
    }

    [Fact]
    public async Task QueryString_ApiKey_Works_When_Header_Absent()
    {
        var (_, keys) = Admin();
        var key = keys.Create(TagSet.Empty, null, "k", DateTimeOffset.UtcNow);

        using var client = _factory.CreateClient();
        var res = await client.GetAsync(new Uri($"/v1/conf?apiKey={Uri.EscapeDataString(key.Plaintext)}", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
