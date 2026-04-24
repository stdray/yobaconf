using YobaConf.Core.Bindings;

namespace YobaConf.Core.Auth;

// Hot-path: every /v1/conf request hits Validate → constant-time hash compare, then subset
// check. Stateless — the store owns identity + scope, the caller applies the scope against
// the incoming tag-vector.
public interface IApiKeyStore
{
    ApiKeyValidation Validate(string? plaintextToken);

    // Confirms the tag-vector satisfies the key's scope. Returns null on success, a reason
    // string (for 403 body) on failure.
    static string? CheckScope(ApiKey key, IReadOnlyDictionary<string, string> tagVector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tagVector);
        return key.RequiredTags.IsSubsetOf(tagVector)
            ? null
            : $"api-key requires tag-vector ⊇ {key.RequiredTags.CanonicalJson}; request vector does not.";
    }
}

public interface IApiKeyAdmin
{
    ApiKeyCreated Create(TagSet requiredTags, IReadOnlyList<string>? allowedKeyPrefixes, string description, DateTimeOffset at, string actor = "system");

    IReadOnlyList<ApiKeyInfo> ListActive();

    bool SoftDelete(long id, DateTimeOffset at, string actor = "system");
}
