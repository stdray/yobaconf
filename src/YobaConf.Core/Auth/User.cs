namespace YobaConf.Core.Auth;

// Cookie-auth admin. PasswordHash is the PBKDF2 encoded form emitted by
// AdminPasswordHasher — never plaintext. In MVP every user has identical rights
// (full CRUD on bindings / api-keys / users / audit), spec §3.
public sealed record User(string Username, string PasswordHash, DateTimeOffset CreatedAt);

public interface IUserStore
{
    User? FindByUsername(string username);
    IReadOnlyList<User> ListAll();
    bool HasAny();

    // Verifies `plaintextPassword` against the user's stored hash. Returns false for unknown
    // usernames; implementations must consume equivalent CPU time on the miss-path (dummy
    // PBKDF2 verify) so attackers cannot enumerate usernames by response timing.
    bool VerifyPassword(string username, string plaintextPassword);
}

public interface IUserAdmin
{
    // Throws if a user with the same username already exists.
    void Create(string username, string plaintextPassword, DateTimeOffset at, string actor = "system");

    // Returns false when the row isn't found; used by the UI to distinguish 404 from 200.
    bool UpdatePassword(string username, string plaintextPassword, DateTimeOffset at, string actor = "system");

    bool Delete(string username, DateTimeOffset at, string actor = "system");
}
