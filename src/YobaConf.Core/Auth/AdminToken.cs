namespace YobaConf.Core.Auth;

// Personal access tokens for the admin JSON API (`/v1/admin/*`, spec §8). Each token is
// owned by a User and grants the same full-CRUD rights a cookie-session does — they
// differ only in transport (header vs cookie). Multi-token per user is by design: separate
// tokens per machine / script let granular revoke without cross-impact, and rotation runs
// as create-new → deploy → revoke-old.
//
// Storage shape mirrors ApiKey: sha256-hex `TokenHash` + 6-char `TokenPrefix` for display.
// `IsDeleted` is set only on self-revoke from `/Admin/Profile`; cascade-on-user-delete is
// a hard delete (see SqliteUserStore.Delete) — token rows never outlive their owner.
public sealed record AdminToken
{
    public required long Id { get; init; }
    public required string Username { get; init; }
    public required string TokenPrefix { get; init; }
    public required string TokenHash { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool IsDeleted { get; init; }
}

// UI-facing snapshot — strips TokenHash, keeps Username + Description + prefix for the
// `/Admin/Profile` listing.
public sealed record AdminTokenInfo(
    long Id,
    string Username,
    string TokenPrefix,
    string Description,
    DateTimeOffset UpdatedAt);

// Returned from Create exactly once. Caller must surface `Plaintext` to the operator
// immediately and never persist it. The stored row carries only the hash + prefix.
public sealed record AdminTokenCreated(AdminTokenInfo Info, string Plaintext);

// Hot-path validation result. Success carries the AdminToken so the auth middleware can
// wire `HttpContext.User` to the resolved Username without re-fetching.
public abstract record AdminTokenValidation
{
    public sealed record Valid(AdminToken Token) : AdminTokenValidation;
    public sealed record Invalid(string Reason) : AdminTokenValidation;
}

public interface IAdminTokenStore
{
    AdminTokenValidation Validate(string? plaintextToken);
}

public interface IAdminTokenAdmin
{
    // Issues a new token for `username`. Returns the plaintext exactly once — the caller
    // surfaces it to the operator (UI / API response). Storage keeps only the hash.
    AdminTokenCreated Create(string username, string description, DateTimeOffset at, string actor = "system");

    // Lists every live token owned by `username`. `/Admin/Profile` filters to the
    // currently-logged-in user so admins can see only their own tokens.
    IReadOnlyList<AdminTokenInfo> ListByUsername(string username);

    // Self-revoke from `/Admin/Profile`. Sets IsDeleted=1, row stays for audit.
    // Returns false if the token doesn't exist or is already deleted.
    bool SoftDelete(long id, DateTimeOffset at, string actor = "system");
}
