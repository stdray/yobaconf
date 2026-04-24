using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Resolve;

public sealed class ResponseTemplateTests
{
    static Binding Plain(TagSet t, string k, string v, Dictionary<string, string>? aliases = null) => new()
    {
        Id = 0,
        TagSet = t,
        KeyPath = k,
        Kind = BindingKind.Plain,
        ValuePlain = v,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Aliases = aliases,
    };

    [Theory]
    [InlineData("db.host", ResponseTemplate.Dotnet, "db__host")]
    [InlineData("cache.policy.lru", ResponseTemplate.Dotnet, "cache__policy__lru")]
    [InlineData("log-level", ResponseTemplate.Dotnet, "log-level")]
    [InlineData("db.host", ResponseTemplate.Envvar, "DB_HOST")]
    [InlineData("log-level", ResponseTemplate.Envvar, "LOG_LEVEL")]
    [InlineData("cache.policy.lru", ResponseTemplate.Envvar, "CACHE_POLICY_LRU")]
    [InlineData("db.host", ResponseTemplate.EnvvarDeep, "DB__HOST")]
    [InlineData("log-level", ResponseTemplate.EnvvarDeep, "LOG_LEVEL")]
    [InlineData("cache.policy.lru", ResponseTemplate.EnvvarDeep, "CACHE__POLICY__LRU")]
    [InlineData("db.host", ResponseTemplate.Flat, "db.host")]
    public void Derive_MapsKeyPath(string keyPath, ResponseTemplate template, string expected) =>
        ResponseTemplateParser.Derive(keyPath, template).Should().Be(expected);

    [Fact]
    public void Parse_Accepts_KnownNames_And_Rejects_Unknown()
    {
        ResponseTemplateParser.Parse(null).Should().Be(ResponseTemplate.Flat);
        ResponseTemplateParser.Parse("").Should().Be(ResponseTemplate.Flat);
        ResponseTemplateParser.Parse("flat").Should().Be(ResponseTemplate.Flat);
        ResponseTemplateParser.Parse("dotnet").Should().Be(ResponseTemplate.Dotnet);
        ResponseTemplateParser.Parse("envvar").Should().Be(ResponseTemplate.Envvar);
        ResponseTemplateParser.Parse("envvar-deep").Should().Be(ResponseTemplate.EnvvarDeep);
        ResponseTemplateParser.Parse("envvar_deep").Should().Be(ResponseTemplate.EnvvarDeep);

        FluentActions.Invoking(() => ResponseTemplateParser.Parse("xxx"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_Flat_NestsJson()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "db.port", "5432"));

        var result = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Flat);
        result.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"db":{"host":"x","port":5432}}""");
    }

    [Fact]
    public void Resolve_Dotnet_Produces_FlatJsonWithDoubleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "db.port", "5432"));

        var result = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Dotnet);
        result.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"db__host":"x","db__port":5432}""");
    }

    [Fact]
    public void Resolve_Envvar_UppercasesAndSingleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "log-level", "\"Info\""));

        var result = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Envvar);
        result.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"DB_HOST":"x","LOG_LEVEL":"Info"}""");
    }

    [Fact]
    public void Resolve_EnvvarDeep_Nests_Dots_As_DoubleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "cache.policy.lru", "true"));

        var result = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.EnvvarDeep);
        result.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"CACHE__POLICY__LRU":true,"DB__HOST":"x"}""");
    }

    [Fact]
    public void AliasOverride_Wins_OverDerivedKey()
    {
        // A binding whose envvar template should literally be "AWS_ACCESS_KEY_ID" — a name
        // the platform mandates but our default derivation (AWS-ACCESS-KEY-ID → uppercase
        // with dashes) wouldn't emit.
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(
            TagSet.Empty, "aws-access-key-id", "\"AKIA…\"",
            aliases: new Dictionary<string, string> { ["envvar"] = "AWS_ACCESS_KEY_ID" }));
        store.Upsert(Plain(TagSet.Empty, "other", "\"x\""));

        var result = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Envvar);
        var json = result.Should().BeOfType<ResolveSuccess>().Subject.Json;
        json.Should().Contain("\"AWS_ACCESS_KEY_ID\":\"AKIA…\"");
        json.Should().NotContain("AWS-ACCESS-KEY-ID");
    }

    [Fact]
    public void AliasOverride_Is_PerTemplate()
    {
        // Alias overrides only kick in for the template they target; the default
        // derivation is used for other templates.
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(
            TagSet.Empty, "db.host", "\"x\"",
            aliases: new Dictionary<string, string> { ["envvar"] = "PGHOST" }));

        var envvar = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Envvar);
        ((ResolveSuccess)envvar).Json.Should().Be("""{"PGHOST":"x"}""");

        var dotnet = new ResolvePipeline(store).Resolve(new Dictionary<string, string>(), null, ResponseTemplate.Dotnet);
        ((ResolveSuccess)dotnet).Json.Should().Be("""{"db__host":"x"}""");
    }
}
