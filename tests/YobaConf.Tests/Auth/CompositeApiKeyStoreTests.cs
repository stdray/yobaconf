using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Auth;

public sealed class CompositeApiKeyStoreTests
{
    sealed class StubStore(string acceptToken, string description) : IApiKeyStore
    {
        public ApiKeyValidation Validate(string? plaintextToken) =>
            plaintextToken == acceptToken
                ? new ApiKeyValidation.Valid(new ApiKey
                {
                    Id = -1,
                    TokenPrefix = description,
                    TokenHash = "hash-" + description,
                    RequiredTags = TagSet.Empty,
                    AllowedKeyPrefixes = null,
                    Description = description,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                })
                : new ApiKeyValidation.Invalid("unknown api-key");
    }

    [Fact]
    public void Validate_Returns_FirstStore_Match()
    {
        var composite = new CompositeApiKeyStore(
            new StubStore("first-token", "first"),
            new StubStore("second-token", "second"));

        var valid = (ApiKeyValidation.Valid)composite.Validate("first-token");
        valid.Key.Description.Should().Be("first");
    }

    [Fact]
    public void Validate_Falls_Through_To_SecondStore()
    {
        var composite = new CompositeApiKeyStore(
            new StubStore("first-token", "first"),
            new StubStore("second-token", "second"));

        var valid = (ApiKeyValidation.Valid)composite.Validate("second-token");
        valid.Key.Description.Should().Be("second");
    }

    [Fact]
    public void Validate_Fails_When_NoStore_Matches()
    {
        var composite = new CompositeApiKeyStore(
            new StubStore("a", "first"),
            new StubStore("b", "second"));

        composite.Validate("nope").Should().BeOfType<ApiKeyValidation.Invalid>();
    }

    [Fact]
    public void Validate_Fails_On_Null_Or_Empty_Without_Calling_InnerStores()
    {
        var alwaysThrows = new ThrowingStore();
        var composite = new CompositeApiKeyStore(alwaysThrows);

        composite.Validate(null).Should().BeOfType<ApiKeyValidation.Invalid>();
        composite.Validate("").Should().BeOfType<ApiKeyValidation.Invalid>();
        alwaysThrows.Calls.Should().Be(0);
    }

    [Fact]
    public void Validate_Fails_When_NoStores_Configured() =>
        new CompositeApiKeyStore().Validate("any").Should().BeOfType<ApiKeyValidation.Invalid>();

    sealed class ThrowingStore : IApiKeyStore
    {
        public int Calls { get; private set; }
        public ApiKeyValidation Validate(string? plaintextToken)
        {
            Calls++;
            throw new InvalidOperationException("should not be called");
        }
    }
}
