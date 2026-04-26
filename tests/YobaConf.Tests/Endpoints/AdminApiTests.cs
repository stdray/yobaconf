using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Audit;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Endpoints;

// Integration tests for /v1/admin/* — token auth (G.2) + bindings CRUD (G.3) + api-keys
// CRUD (G.4). Each test gets a fresh tmp DB through WebApplicationFactory; admin tokens
// are provisioned via the IAdminTokenAdmin service before the request.
public sealed class AdminApiTests : IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _tmpDir;

    public AdminApiTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-admin-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDirectory"] = _tmpDir,
                ["Storage:FileName"] = "yobaconf.db",
            }));
            // Register ISecretEncryptor explicitly so PUT-secret tests don't depend on
            // YOBACONF_MASTER_KEY config flow (the test runner deliberately doesn't load
            // user-secrets / env vars). 32-byte all-zero key — fine for round-trip
            // encrypt/decrypt assertions, never used outside tests.
            builder.ConfigureServices(svc => svc.AddSingleton<Core.Security.ISecretEncryptor>(
                new Core.Security.AesGcmSecretEncryptor(Convert.ToBase64String(new byte[32]))));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    string ProvisionAdminToken(string username = "alice")
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdmin>();
        try { users.Create(username, "test-pw", DateTimeOffset.UnixEpoch); }
        catch (InvalidOperationException) { /* user already exists across tests sharing the factory */ }
        var tokens = scope.ServiceProvider.GetRequiredService<IAdminTokenAdmin>();
        return tokens.Create(username, "test-token", DateTimeOffset.UnixEpoch).Plaintext;
    }

    static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // ---- G.2: auth ----

    [Fact]
    public async Task MissingToken_Returns_401()
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongToken_Returns_401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "absolutely-wrong-token-22");
        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SoftDeletedToken_Returns_401()
    {
        // Create a token, soft-delete it, attempt to use it.
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdmin>();
        users.Create("bob", "pw", DateTimeOffset.UnixEpoch);
        var tokenAdmin = scope.ServiceProvider.GetRequiredService<IAdminTokenAdmin>();
        var created = tokenAdmin.Create("bob", "rotate-me", DateTimeOffset.UnixEpoch);
        tokenAdmin.SoftDelete(created.Info.Id, DateTimeOffset.UnixEpoch);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Plaintext);
        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BearerHeader_Auth_Works()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task XAdminTokenHeader_Auth_Works()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-YobaConf-AdminToken", token);

        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryStringFallback_Auth_Works()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        var url = $"/v1/admin/bindings?adminToken={Uri.EscapeDataString(token)}";
        var res = await client.GetAsync(new Uri(url, UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BearerAndXHeader_WithDifferentValues_Returns_400_AmbiguousAuth()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-YobaConf-AdminToken", "different-token-22charsxx");

        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("ambiguous_auth");
    }

    [Fact]
    public async Task BearerAndXHeader_WithSameValue_Is_OK()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-YobaConf-AdminToken", token);

        var res = await client.GetAsync(new Uri("/v1/admin/bindings", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- G.3: bindings CRUD ----

    [Fact]
    public async Task PutBinding_Creates_Plain_Returns_201()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.host",
            kind = "Plain",
            value = "prod-db",
        }));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("etag", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PutBinding_Update_Existing_Returns_200_NotCreated()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.host",
            kind = "Plain",
            value = "v1",
        }));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.host",
            kind = "Plain",
            value = "v2",
        }));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PutBinding_Secret_Encrypts_Value()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.password",
            kind = "Secret",
            value = "supersecret",
        }));
        res.StatusCode.Should().Be(HttpStatusCode.Created);

        // Retrieve via the list — secret value must be redacted as null.
        var list = await client.GetAsync(new Uri("/v1/admin/bindings?key=db.password", UriKind.Relative));
        var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        arr.GetArrayLength().Should().Be(1);
        var item = arr[0];
        item.GetProperty("kind").GetString().Should().Be("Secret");
        item.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PutBinding_InvalidSlug_Returns_400()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { Env = "prod" }, // uppercase tag-key violates slug
            keyPath = "db.host",
            kind = "Plain",
            value = "x",
        }));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteBinding_SoftDeletes_AndAuditsAdminTokenActor()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var put = await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.host",
            kind = "Plain",
            value = "x",
        }));
        var id = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var del = await client.DeleteAsync(new Uri($"/v1/admin/bindings/{id}", UriKind.Relative));
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Repeat-delete returns 404.
        var del2 = await client.DeleteAsync(new Uri($"/v1/admin/bindings/{id}", UriKind.Relative));
        del2.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Audit actor format: <Username>:admin-token:<TokenPrefix>
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogStore>();
        var entries = audit.Query(AuditEntityType.Binding, null, null, 100);
        entries.Should().Contain(e => e.Action == AuditAction.Deleted && e.Actor.StartsWith("alice:admin-token:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListBindings_FilterByTag_AndKey_Prefix()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.host",
            kind = "Plain",
            value = "p",
        }));
        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "db.port",
            kind = "Plain",
            value = 5432,
        }));
        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "cache.ttl",
            kind = "Plain",
            value = 60,
        }));
        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "dev" },
            keyPath = "db.host",
            kind = "Plain",
            value = "d",
        }));

        // Filter by tag env=prod AND key prefix db. → 2 rows.
        var res = await client.GetAsync(new Uri("/v1/admin/bindings?tag=env=prod&key=db.", UriKind.Relative));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        arr.GetArrayLength().Should().Be(2);
        foreach (var item in arr.EnumerateArray())
        {
            item.GetProperty("keyPath").GetString().Should().StartWith("db.");
            item.GetProperty("tagSet").GetProperty("env").GetString().Should().Be("prod");
        }
    }

    [Fact]
    public async Task ListBindings_Plain_Value_Roundtrips_TypedJson()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "n",
            kind = "Plain",
            value = 42,
        }));
        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "b",
            kind = "Plain",
            value = true,
        }));
        await client.PutAsync(new Uri("/v1/admin/bindings", UriKind.Relative), Json(new
        {
            tagSet = new { env = "prod" },
            keyPath = "s",
            kind = "Plain",
            value = "x",
        }));

        var res = await client.GetAsync(new Uri("/v1/admin/bindings?tag=env=prod", UriKind.Relative));
        var arr = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("keyPath").GetString()!, e => e.GetProperty("value"));
        arr["n"].GetInt32().Should().Be(42);
        arr["b"].GetBoolean().Should().BeTrue();
        arr["s"].GetString().Should().Be("x");
    }

    // ---- G.4: api-keys CRUD ----

    [Fact]
    public async Task PutApiKey_Returns_Plaintext_Once_AndPersistsHash()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PutAsync(new Uri("/v1/admin/api-keys", UriKind.Relative), Json(new
        {
            description = "prod runtime",
            requiredTags = new { env = "prod" },
            allowedKeyPrefixes = new[] { "db.", "cache." },
        }));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var plaintext = doc.RootElement.GetProperty("plaintext").GetString();
        plaintext.Should().NotBeNullOrEmpty();
        plaintext!.Length.Should().Be(22);
        doc.RootElement.GetProperty("prefix").GetString().Should().Be(plaintext[..6]);

        // List doesn't surface plaintext.
        var list = await client.GetAsync(new Uri("/v1/admin/api-keys", UriKind.Relative));
        var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        arr.GetArrayLength().Should().Be(1);
        var item = arr[0];
        item.TryGetProperty("plaintext", out _).Should().BeFalse();
        item.GetProperty("prefix").GetString().Should().Be(plaintext[..6]);
    }

    [Fact]
    public async Task DeleteApiKey_SoftDeletes()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var put = await client.PutAsync(new Uri("/v1/admin/api-keys", UriKind.Relative), Json(new
        {
            description = "throwaway",
            requiredTags = new { env = "prod" },
            allowedKeyPrefixes = (string[]?)null,
        }));
        var id = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var del = await client.DeleteAsync(new Uri($"/v1/admin/api-keys/{id}", UriKind.Relative));
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await client.GetAsync(new Uri("/v1/admin/api-keys", UriKind.Relative));
        var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        arr.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PutApiKey_MissingDescription_Returns_400()
    {
        var token = ProvisionAdminToken();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PutAsync(new Uri("/v1/admin/api-keys", UriKind.Relative), Json(new
        {
            description = "",
            requiredTags = new { env = "prod" },
        }));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
