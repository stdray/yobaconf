using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Core.Security;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Resolve;

public sealed class ResolvePipelineTests
{
    // 32 bytes of 0x42 base64 — shared with AesGcmSecretEncryptorTests, stable for snapshots.
    const string TestKeyBase64 = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=";

    static Binding Plain(TagSet tags, string keyPath, string valueJson) => new()
    {
        Id = 0,
        TagSet = tags,
        KeyPath = keyPath,
        Kind = BindingKind.Plain,
        ValuePlain = valueJson,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void ZeroTag_RootBinding_ResolvesForAnyTagVector()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "log-format", "\"json\""));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string> { ["env"] = "prod" });

        var success = outcome.Should().BeOfType<ResolveSuccess>().Subject;
        success.Json.Should().Be("""{"log-format":"json"}""");
        success.ETag.Should().HaveLength(16);
    }

    [Fact]
    public void SingleDimension_MoreSpecific_OverridesLessSpecific()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "log-level", "\"Warn\""));
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string> { ["env"] = "prod" });

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"log-level":"Info"}""");
    }

    [Fact]
    public void MultiDimension_HigherTagCount_WinsOverLower()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""));
        store.Upsert(Plain(TagSet.From([new("env", "prod"), new("project", "yobapub")]), "log-level", "\"Debug\""));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"log-level":"Debug"}""");
    }

    [Fact]
    public void TiedAtMax_IdenticalValues_DeterministicPickLowestId()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        // Two incomparable tag-sets with same TagCount and same value — deterministic pick
        // (spec §4 "pick any, deterministic: lowest Id"). Neither value differs so it's
        // not a real conflict.
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "x", "\"a\""));
        store.Upsert(Plain(TagSet.From([new("project", "yobapub")]), "x", "\"a\""));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"x":"a"}""");
    }

    [Fact]
    public void TiedAtMax_DivergingValues_YieldsConflict()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var first = store.Upsert(Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""));
        var second = store.Upsert(Plain(TagSet.From([new("project", "yobapub")]), "log-level", "\"Debug\""));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        var conflict = outcome.Should().BeOfType<ResolveConflict>().Subject;
        conflict.KeyPath.Should().Be("log-level");
        conflict.Candidates.Should().HaveCount(2);
        conflict.Candidates.Select(c => c.BindingId).Should().BeEquivalentTo([first.Binding.Id, second.Binding.Id]);
        conflict.Candidates.Select(c => c.ValueDisplay).Should().Contain("\"Info\"").And.Contain("\"Debug\"");
    }

    [Fact]
    public void Secret_DecryptedOnResolve_And_ReturnedAsJsonString()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var enc = new AesGcmSecretEncryptor(TestKeyBase64);
        var bundle = enc.Encrypt("s3cr3t-p4ss");

        store.Upsert(new Binding
        {
            Id = 0,
            TagSet = TagSet.From([new("env", "prod")]),
            KeyPath = "db.password",
            Kind = BindingKind.Secret,
            Ciphertext = bundle.Ciphertext,
            Iv = bundle.Iv,
            AuthTag = bundle.AuthTag,
            KeyVersion = bundle.KeyVersion,
            ContentHash = string.Empty,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var outcome = new ResolvePipeline(store, enc).Resolve(new Dictionary<string, string> { ["env"] = "prod" });

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"db":{"password":"s3cr3t-p4ss"}}""");
    }

    [Fact]
    public void Secret_Without_Encryptor_Throws()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(new Binding
        {
            Id = 0,
            TagSet = TagSet.Empty,
            KeyPath = "k",
            Kind = BindingKind.Secret,
            Ciphertext = [1, 2, 3],
            Iv = new byte[12],
            AuthTag = new byte[16],
            KeyVersion = "v1",
            ContentHash = string.Empty,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        FluentActions.Invoking(() => new ResolvePipeline(store).Resolve(new Dictionary<string, string>()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*YOBACONF_MASTER_KEY*");
    }

    [Fact]
    public void DottedKeys_Expand_Into_NestedJson()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "db.port", "5432"));
        store.Upsert(Plain(TagSet.Empty, "cache.policy.lru", "true"));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string>());

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"cache":{"policy":{"lru":true}},"db":{"host":"x","port":5432}}""");
    }

    [Fact]
    public void ETag_Stable_Across_InsertOrderPermutations()
    {
        // Same content, different Upsert sequence → identical canonical JSON → identical ETag.
        // Load-bearing for 304 cache responses (spec §4.7).
        using var tmp1 = new TempDb();
        var storeA = tmp1.CreateStore();
        storeA.Upsert(Plain(TagSet.Empty, "a", "1"));
        storeA.Upsert(Plain(TagSet.Empty, "b", "2"));
        storeA.Upsert(Plain(TagSet.Empty, "c", "3"));

        using var tmp2 = new TempDb();
        var storeB = tmp2.CreateStore();
        storeB.Upsert(Plain(TagSet.Empty, "c", "3"));
        storeB.Upsert(Plain(TagSet.Empty, "a", "1"));
        storeB.Upsert(Plain(TagSet.Empty, "b", "2"));

        var a = new ResolvePipeline(storeA).Resolve(new Dictionary<string, string>());
        var b = new ResolvePipeline(storeB).Resolve(new Dictionary<string, string>());

        var successA = a.Should().BeOfType<ResolveSuccess>().Subject;
        var successB = b.Should().BeOfType<ResolveSuccess>().Subject;
        successA.Json.Should().Be(successB.Json);
        successA.ETag.Should().Be(successB.ETag);
    }

    [Fact]
    public void EmptyCandidateSet_YieldsEmptyObject()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "k", "\"v\""));

        // Request doesn't match any binding.
        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string> { ["env"] = "staging" });

        var success = outcome.Should().BeOfType<ResolveSuccess>().Subject;
        success.Json.Should().Be("{}");
        success.ETag.Should().HaveLength(16);
    }

    [Fact]
    public void CanonicalJson_KeysAreOrdinalSorted_AtEachLevel()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        // Insert in reverse-alphabetical order — canonical output must re-order.
        store.Upsert(Plain(TagSet.Empty, "z", "1"));
        store.Upsert(Plain(TagSet.Empty, "a", "2"));
        store.Upsert(Plain(TagSet.Empty, "m.b", "3"));
        store.Upsert(Plain(TagSet.Empty, "m.a", "4"));

        var outcome = new ResolvePipeline(store).Resolve(new Dictionary<string, string>());

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"a":2,"m":{"a":4,"b":3},"z":1}""");
    }

    // ---- Phase C.1: non-Flat template snapshots ----

    [Fact]
    public void Template_Dotnet_ProducesFlatWithDoubleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "db.port", "5432"));

        var outcome = new ResolvePipeline(store).Resolve(
            new Dictionary<string, string>(),
            allowedKeyPrefixes: null,
            template: ResponseTemplate.Dotnet);

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"db__host":"x","db__port":5432}""");
    }

    [Fact]
    public void Template_Envvar_UppercasesAndSingleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "log-level", "\"Info\""));

        var outcome = new ResolvePipeline(store).Resolve(
            new Dictionary<string, string>(),
            allowedKeyPrefixes: null,
            template: ResponseTemplate.Envvar);

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"DB_HOST":"x","LOG_LEVEL":"Info"}""");
    }

    [Fact]
    public void Template_EnvvarDeep_DotsBecomeDoubleUnderscore()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""));
        store.Upsert(Plain(TagSet.Empty, "cache.policy.lru", "true"));

        var outcome = new ResolvePipeline(store).Resolve(
            new Dictionary<string, string>(),
            allowedKeyPrefixes: null,
            template: ResponseTemplate.EnvvarDeep);

        outcome.Should().BeOfType<ResolveSuccess>()
            .Which.Json.Should().Be("""{"CACHE__POLICY__LRU":true,"DB__HOST":"x"}""");
    }

    [Fact]
    public void Template_AliasOverride_WinsInPipeline()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(new Binding
        {
            Id = 0,
            TagSet = TagSet.Empty,
            KeyPath = "aws-access-key-id",
            Kind = BindingKind.Plain,
            ValuePlain = "\"AKIA1234\"",
            ContentHash = string.Empty,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Aliases = new Dictionary<string, string> { ["envvar"] = "AWS_ACCESS_KEY_ID" },
        });
        store.Upsert(Plain(TagSet.Empty, "other", "\"x\""));

        var outcome = new ResolvePipeline(store).Resolve(
            new Dictionary<string, string>(),
            allowedKeyPrefixes: null,
            template: ResponseTemplate.Envvar);

        var json = outcome.Should().BeOfType<ResolveSuccess>().Subject.Json;
        json.Should().Contain("\"AWS_ACCESS_KEY_ID\":\"AKIA1234\"");
        json.Should().NotContain("AWS-ACCESS-KEY-ID");
    }

    [Fact]
    public void Secret_Tied_Always_Conflicts_EvenIfSecretCiphertextHappenedToMatch()
    {
        // Conservative rule: any Secret involved in a tied-at-max bucket yields a conflict
        // regardless of plaintext equivalence. Admin must write a more-specific overlay.
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var enc = new AesGcmSecretEncryptor(TestKeyBase64);

        var bundleA = enc.Encrypt("same-secret");
        var bundleB = enc.Encrypt("same-secret"); // different IV → different ciphertext anyway

        store.Upsert(new Binding
        {
            Id = 0,
            TagSet = TagSet.From([new("env", "prod")]),
            KeyPath = "k",
            Kind = BindingKind.Secret,
            Ciphertext = bundleA.Ciphertext,
            Iv = bundleA.Iv,
            AuthTag = bundleA.AuthTag,
            KeyVersion = bundleA.KeyVersion,
            ContentHash = string.Empty,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        store.Upsert(new Binding
        {
            Id = 0,
            TagSet = TagSet.From([new("project", "yobapub")]),
            KeyPath = "k",
            Kind = BindingKind.Secret,
            Ciphertext = bundleB.Ciphertext,
            Iv = bundleB.Iv,
            AuthTag = bundleB.AuthTag,
            KeyVersion = bundleB.KeyVersion,
            ContentHash = string.Empty,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var outcome = new ResolvePipeline(store, enc).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        var conflict = outcome.Should().BeOfType<ResolveConflict>().Subject;
        conflict.Candidates.Should().AllSatisfy(c => c.ValueDisplay.Should().Be("<secret>"));
    }
}
