using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using YobaConf.Core.Bindings;

namespace YobaConf.Core.Auth;

// In-memory IApiKeyStore backed by appsettings (BootstrapApiKeyOptions). Read-only —
// does not implement IApiKeyAdmin since lifecycle is owned by the config source.
//
// Companion to SqliteApiKeyStore via CompositeApiKeyStore: bootstrap keys for self-host
// startup, SQLite for keys minted via the admin UI. Mirrors the role of yobalog's
// ConfigApiKeyStore but builds the full ApiKey record (RequiredTags + AllowedKeyPrefixes)
// since yobaconf scope-checks per request.
//
// Tokens hash on load → dict keyed by hash, same constant-time comparison flow as
// SqliteApiKeyStore.Validate so the two paths are indistinguishable from the outside.
// Synthetic Ids are negative + monotonically decreasing to never collide with SQLite
// rowids (positive autoincrement); audit entries from these keys still sort cleanly
// by Id since negative < every positive.
public sealed class ConfigApiKeyStore : IApiKeyStore
{
    readonly ImmutableDictionary<string, ApiKey> byHash;

    public ConfigApiKeyStore(IOptions<BootstrapApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var loadedAt = DateTimeOffset.UtcNow;
        var builder = ImmutableDictionary.CreateBuilder<string, ApiKey>(StringComparer.Ordinal);
        long syntheticId = -1;

        foreach (var entry in options.Value.Keys)
        {
            if (string.IsNullOrEmpty(entry.Token))
                continue;

            var hash = ApiKeyTokenGenerator.HashHex(entry.Token);
            // Skip duplicates rather than throw — config sources can stack (appsettings +
            // env-vars + user-secrets) and a duplicate token across them is a no-op, not
            // an error. First wins; admin observes via /Admin/ApiKeys listing absence.
            if (builder.ContainsKey(hash))
                continue;

            var requiredTags = entry.RequiredTags.Count == 0
                ? TagSet.Empty
                : TagSet.From(entry.RequiredTags);
            var allowedPrefixes = entry.AllowedKeyPrefixes is { Count: > 0 } prefixes
                ? prefixes
                : null;

            builder[hash] = new ApiKey
            {
                Id = syntheticId--,
                TokenPrefix = ApiKeyTokenGenerator.Prefix(entry.Token),
                TokenHash = hash,
                RequiredTags = requiredTags,
                AllowedKeyPrefixes = allowedPrefixes,
                Description = entry.Description,
                UpdatedAt = loadedAt,
                IsDeleted = false,
            };
        }

        byHash = builder.ToImmutable();
    }

    public ApiKeyValidation Validate(string? plaintextToken)
    {
        if (string.IsNullOrEmpty(plaintextToken))
            return new ApiKeyValidation.Invalid("missing api-key");

        var hash = ApiKeyTokenGenerator.HashHex(plaintextToken);
        if (!byHash.TryGetValue(hash, out var key))
            return new ApiKeyValidation.Invalid("unknown api-key");

        // Constant-time double-check matches SqliteApiKeyStore.Validate. The dict lookup
        // already implies a hash match; the explicit compare keeps the two paths
        // observationally identical (timing side-channels, error messages).
        var stored = Convert.FromHexString(key.TokenHash);
        var candidate = Convert.FromHexString(hash);
        return CryptographicOperations.FixedTimeEquals(stored, candidate)
            ? new ApiKeyValidation.Valid(key)
            : new ApiKeyValidation.Invalid("unknown api-key");
    }
}
