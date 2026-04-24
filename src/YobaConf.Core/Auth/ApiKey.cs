using YobaConf.Core.Bindings;

namespace YobaConf.Core.Auth;

// Domain-facing API key. `TokenPrefix` is the first 6 chars of the plaintext, shown in
// admin UI for identification. `TokenHash` is sha256-hex of the plaintext — never
// reversible back. `RequiredTags` must be a subset of every validated request's tag-vector;
// `AllowedKeyPrefixes`, when non-null, filters the resolved response to bindings whose
// KeyPath prefix-matches at least one entry.
public sealed record ApiKey
{
    public required long Id { get; init; }
    public required string TokenPrefix { get; init; }
    public required string TokenHash { get; init; }
    public required TagSet RequiredTags { get; init; }
    public required IReadOnlyList<string>? AllowedKeyPrefixes { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool IsDeleted { get; init; }
}

// Snapshot returned to admin UI — strips TokenHash, keeps prefix + scope for display.
public sealed record ApiKeyInfo(
    long Id,
    string TokenPrefix,
    TagSet RequiredTags,
    IReadOnlyList<string>? AllowedKeyPrefixes,
    string Description,
    DateTimeOffset UpdatedAt);

// Returned from Create exactly once — caller must surface the `Plaintext` to the operator
// immediately and never persist it. The stored row carries only the hash + prefix.
public sealed record ApiKeyCreated(ApiKeyInfo Info, string Plaintext);

// Hot-path validation result. Success carries the identified ApiKey so callers can apply
// the scope filter (RequiredTags + AllowedKeyPrefixes) without re-fetching.
public abstract record ApiKeyValidation
{
    public sealed record Valid(ApiKey Key) : ApiKeyValidation;
    public sealed record Invalid(string Reason) : ApiKeyValidation;
}
