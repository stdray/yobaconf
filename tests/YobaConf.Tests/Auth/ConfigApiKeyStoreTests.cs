using Microsoft.Extensions.Options;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Auth;

public sealed class ConfigApiKeyStoreTests
{
    static ConfigApiKeyStore CreateStore(params BootstrapApiKeyEntry[] keys) =>
        new(Options.Create(new BootstrapApiKeyOptions { Keys = keys }));

    [Fact]
    public void Validate_Success_On_KnownToken()
    {
        var store = CreateStore(new BootstrapApiKeyEntry
        {
            Token = "abc123def456ghi789jklm",
            Description = "yobapub-server",
            RequiredTags = new Dictionary<string, string> { ["env"] = "prod" },
        });

        var outcome = store.Validate("abc123def456ghi789jklm");

        var valid = outcome.Should().BeOfType<ApiKeyValidation.Valid>().Subject;
        valid.Key.Description.Should().Be("yobapub-server");
        valid.Key.RequiredTags.CanonicalJson.Should().Be("{\"env\":\"prod\"}");
        valid.Key.TokenPrefix.Should().Be("abc123");
        valid.Key.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Validate_Fails_On_Null_Empty_OrWrongToken()
    {
        var store = CreateStore(new BootstrapApiKeyEntry { Token = "known-token-xxxxxxxxxx" });

        store.Validate(null).Should().BeOfType<ApiKeyValidation.Invalid>();
        store.Validate(string.Empty).Should().BeOfType<ApiKeyValidation.Invalid>();
        store.Validate("not-a-known-token-yyyy").Should().BeOfType<ApiKeyValidation.Invalid>();
    }

    [Fact]
    public void Validate_Fails_On_EmptyConfig() =>
        CreateStore().Validate("anything").Should().BeOfType<ApiKeyValidation.Invalid>();

    [Fact]
    public void Validate_Skips_Entries_WithEmptyToken()
    {
        var store = CreateStore(
            new BootstrapApiKeyEntry { Token = "", Description = "skipped" },
            new BootstrapApiKeyEntry { Token = "real-token-xxxxxxxxxxx", Description = "real" });

        store.Validate("real-token-xxxxxxxxxxx").Should().BeOfType<ApiKeyValidation.Valid>();
    }

    [Fact]
    public void Validate_DeduplicatesTokens_FirstWins()
    {
        var store = CreateStore(
            new BootstrapApiKeyEntry { Token = "dup-token-zzzzzzzzzzzz", Description = "first" },
            new BootstrapApiKeyEntry { Token = "dup-token-zzzzzzzzzzzz", Description = "second" });

        var valid = (ApiKeyValidation.Valid)store.Validate("dup-token-zzzzzzzzzzzz");
        valid.Key.Description.Should().Be("first");
    }

    [Fact]
    public void RequiredTags_Empty_When_Section_Missing()
    {
        var store = CreateStore(new BootstrapApiKeyEntry { Token = "token-aaaaaaaaaaaaaaaa" });

        var valid = (ApiKeyValidation.Valid)store.Validate("token-aaaaaaaaaaaaaaaa");
        valid.Key.RequiredTags.Count.Should().Be(0);
        valid.Key.RequiredTags.CanonicalJson.Should().Be("{}");
    }

    [Fact]
    public void AllowedKeyPrefixes_Roundtrip()
    {
        var store = CreateStore(new BootstrapApiKeyEntry
        {
            Token = "scoped-token-bbbbbbbbb",
            AllowedKeyPrefixes = ["client.", "public."],
        });

        var valid = (ApiKeyValidation.Valid)store.Validate("scoped-token-bbbbbbbbb");
        valid.Key.AllowedKeyPrefixes.Should().BeEquivalentTo(["client.", "public."]);
    }

    [Fact]
    public void AllowedKeyPrefixes_NormalisedToNull_OnEmptyList()
    {
        var store = CreateStore(new BootstrapApiKeyEntry
        {
            Token = "no-prefixes-token-cccc",
            AllowedKeyPrefixes = [],
        });

        var valid = (ApiKeyValidation.Valid)store.Validate("no-prefixes-token-cccc");
        valid.Key.AllowedKeyPrefixes.Should().BeNull();
    }

    [Fact]
    public void Synthetic_Ids_AreNegative_And_Distinct()
    {
        var store = CreateStore(
            new BootstrapApiKeyEntry { Token = "tok-a-aaaaaaaaaaaaaaa" },
            new BootstrapApiKeyEntry { Token = "tok-b-bbbbbbbbbbbbbbb" },
            new BootstrapApiKeyEntry { Token = "tok-c-ccccccccccccccc" });

        var ids = new[]
        {
            ((ApiKeyValidation.Valid)store.Validate("tok-a-aaaaaaaaaaaaaaa")).Key.Id,
            ((ApiKeyValidation.Valid)store.Validate("tok-b-bbbbbbbbbbbbbbb")).Key.Id,
            ((ApiKeyValidation.Valid)store.Validate("tok-c-ccccccccccccccc")).Key.Id,
        };

        ids.Should().OnlyContain(id => id < 0);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CheckScope_Works_With_ConfigStore_Keys()
    {
        var store = CreateStore(new BootstrapApiKeyEntry
        {
            Token = "scope-token-dddddddddddd",
            RequiredTags = new Dictionary<string, string>
            {
                ["env"] = "prod",
                ["project"] = "yobapub",
            },
        });

        var valid = (ApiKeyValidation.Valid)store.Validate("scope-token-dddddddddddd");

        IApiKeyStore.CheckScope(valid.Key, new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
            ["host"] = "worker-01",
        }).Should().BeNull();

        IApiKeyStore.CheckScope(valid.Key, new Dictionary<string, string>
        {
            ["env"] = "prod",
        }).Should().NotBeNull();
    }
}
