using System.Security.Cryptography;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Auth;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IAdminTokenStore + IAdminTokenAdmin. Token shape and storage pattern are
// identical to SqliteApiKeyStore (sha256 hash, 6-char prefix, constant-time compare on
// validate). What differs:
//   - `Username` ties each token to a User (consistency via handler logic, no SQL FK —
//     SqliteUserStore.Delete calls HardDeleteByUsernameTx in the same transaction so
//     either both succeed or both roll back).
//   - No scope (RequiredTags / AllowedKeyPrefixes) — admin tokens always carry full
//     user-equivalent rights in MVP. Per-token scope is deferred together with RBAC
//     (see open questions in doc/plan.md).
public sealed class SqliteAdminTokenStore : IAdminTokenStore, IAdminTokenAdmin
{
    readonly string dbPath;

    public SqliteAdminTokenStore(IOptions<SqliteBindingStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.DataDirectory))
            throw new InvalidOperationException(
                "Storage:DataDirectory is empty. AdminToken store shares the binding store's DB path.");
        Directory.CreateDirectory(opts.DataDirectory);
        dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

        using var db = Open();
        SqliteSchema.EnsureSchema(db);
    }

    DataConnection Open()
    {
        var db = SQLiteTools.CreateDataConnection(
            $"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
        return db;
    }

    public AdminTokenValidation Validate(string? plaintextToken)
    {
        using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.validate-admin-token");
        if (string.IsNullOrEmpty(plaintextToken))
            return new AdminTokenValidation.Invalid("missing admin-token");

        var hash = ApiKeyTokenGenerator.HashHex(plaintextToken);

        using var db = Open();
        var row = db.GetTable<AdminTokenRow>()
            .FirstOrDefault(r => r.TokenHash == hash && r.IsDeleted == 0);
        if (row is null)
            return new AdminTokenValidation.Invalid("unknown admin-token");

        // The indexed lookup already implies a hash match; the constant-time check
        // double-protects against timing side-channels on the SQL path itself.
        var storedHashBytes = Convert.FromHexString(row.TokenHash);
        var candidateHashBytes = Convert.FromHexString(hash);
        if (!CryptographicOperations.FixedTimeEquals(storedHashBytes, candidateHashBytes))
            return new AdminTokenValidation.Invalid("unknown admin-token");

        return new AdminTokenValidation.Valid(ToDomain(row));
    }

    public AdminTokenCreated Create(string username, string description, DateTimeOffset at, string actor = "system")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(actor);

        using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.create-admin-token");

        var plaintext = ApiKeyTokenGenerator.New();
        var hash = ApiKeyTokenGenerator.HashHex(plaintext);
        var prefix = ApiKeyTokenGenerator.Prefix(plaintext);
        var ts = at.ToUnixTimeMilliseconds();

        using var db = Open();
        using var tx = db.BeginTransaction();
        var id = Convert.ToInt64(db.InsertWithIdentity(new AdminTokenRow
        {
            Username = username,
            TokenHash = hash,
            TokenPrefix = prefix,
            Description = description,
            UpdatedAt = ts,
            IsDeleted = 0,
        }));

        SqliteAuditLogStore.Append(db, new AuditLogRow
        {
            At = ts,
            Actor = actor,
            Action = AuditAction.Created.ToString(),
            EntityType = AuditEntityType.AdminToken.ToString(),
            KeyPath = prefix,
            OldValue = null,
            NewValue = $"{description}|user={username}",
            OldHash = null,
            NewHash = hash,
        });
        tx.Commit();

        return new AdminTokenCreated(
            new AdminTokenInfo(id, username, prefix, description, at),
            plaintext);
    }

    public IReadOnlyList<AdminTokenInfo> ListByUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-admin-tokens");
        using var db = Open();
        var rows = db.GetTable<AdminTokenRow>()
            .Where(r => r.Username == username && r.IsDeleted == 0)
            .OrderBy(r => r.Description)
            .ToArray();
        return [.. rows.Select(r => new AdminTokenInfo(
            r.Id,
            r.Username,
            r.TokenPrefix,
            r.Description,
            DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt)))];
    }

    public bool SoftDelete(long id, DateTimeOffset at, string actor = "system")
    {
        ArgumentNullException.ThrowIfNull(actor);
        using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-admin-token");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var existing = db.GetTable<AdminTokenRow>()
            .FirstOrDefault(r => r.Id == id && r.IsDeleted == 0);
        if (existing is null) return false;

        var ts = at.ToUnixTimeMilliseconds();
        db.GetTable<AdminTokenRow>()
            .Where(r => r.Id == id && r.IsDeleted == 0)
            .Set(r => r.IsDeleted, 1)
            .Set(r => r.UpdatedAt, ts)
            .Update();

        SqliteAuditLogStore.Append(db, new AuditLogRow
        {
            At = ts,
            Actor = actor,
            Action = AuditAction.Deleted.ToString(),
            EntityType = AuditEntityType.AdminToken.ToString(),
            KeyPath = existing.TokenPrefix,
            OldValue = $"{existing.Description}|user={existing.Username}",
            NewValue = null,
            OldHash = existing.TokenHash,
            NewHash = null,
        });
        tx.Commit();
        return true;
    }

    // Cascade hard-delete invoked from SqliteUserStore.Delete inside the same transaction.
    // Joins the user-delete tx so either both operations commit or both roll back. Each
    // affected token row gets its own audit entry (action=Deleted, value tagged with
    // `|cascade=user-delete`) so the history page can render the cascade explicitly.
    // Returns the number of tokens hard-deleted.
    internal static int HardDeleteByUsernameTx(DataConnection db, string username, long tsMillis, string actor)
    {
        var doomed = db.GetTable<AdminTokenRow>()
            .Where(r => r.Username == username)
            .ToArray();
        if (doomed.Length == 0) return 0;

        foreach (var row in doomed)
        {
            SqliteAuditLogStore.Append(db, new AuditLogRow
            {
                At = tsMillis,
                Actor = actor,
                Action = AuditAction.Deleted.ToString(),
                EntityType = AuditEntityType.AdminToken.ToString(),
                KeyPath = row.TokenPrefix,
                OldValue = $"{row.Description}|user={row.Username}|cascade=user-delete",
                NewValue = null,
                OldHash = row.TokenHash,
                NewHash = null,
            });
        }

        db.GetTable<AdminTokenRow>()
            .Where(r => r.Username == username)
            .Delete();

        return doomed.Length;
    }

    static AdminToken ToDomain(AdminTokenRow r) => new()
    {
        Id = r.Id,
        Username = r.Username,
        TokenPrefix = r.TokenPrefix,
        TokenHash = r.TokenHash,
        Description = r.Description,
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt),
        IsDeleted = r.IsDeleted != 0,
    };
}
