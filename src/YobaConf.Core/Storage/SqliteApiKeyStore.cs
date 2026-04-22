using System.Security.Cryptography;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IApiKeyStore + IApiKeyAdmin. Validate() performs constant-time comparison
// against the stored sha256 hash — no plaintext tokens are ever persisted or returned
// after Create. AllowedKeyPrefixes is optional (null = no filter, empty list same as null
// is disallowed to keep admin intent explicit).
public sealed class SqliteApiKeyStore : IApiKeyStore, IApiKeyAdmin
{
	readonly string dbPath;

	public SqliteApiKeyStore(IOptions<SqliteBindingStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.DataDirectory))
			throw new InvalidOperationException(
				"Storage:DataDirectory is empty. Api-key store shares the binding store's DB path.");
		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		// SqliteBindingStore runs AllStatements on its own construction; if it ran first
		// the tables exist. Replaying again is idempotent so the ordering doesn't matter.
		using var db = Open();
		foreach (var stmt in SqliteSchema.AllStatements)
			db.Execute(stmt);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection(
			$"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		db.Execute("PRAGMA journal_mode=WAL;");
		return db;
	}

	public ApiKeyValidation Validate(string? plaintextToken)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.validate-api-key");
		if (string.IsNullOrEmpty(plaintextToken))
			return new ApiKeyValidation.Invalid("missing api-key");

		var hash = ApiKeyTokenGenerator.HashHex(plaintextToken);

		using var db = Open();
		var row = db.GetTable<ApiKeyRow>()
			.FirstOrDefault(r => r.TokenHash == hash && r.IsDeleted == 0);
		if (row is null)
			return new ApiKeyValidation.Invalid("unknown api-key");

		// The initial table lookup already implies a hash match, but callers that took
		// plaintext from an untrusted channel benefit from a constant-time double-check
		// to avoid leaking timing info about token structure via the SQL path.
		var storedHashBytes = Convert.FromHexString(row.TokenHash);
		var candidateHashBytes = Convert.FromHexString(hash);
		if (!CryptographicOperations.FixedTimeEquals(storedHashBytes, candidateHashBytes))
			return new ApiKeyValidation.Invalid("unknown api-key");

		return new ApiKeyValidation.Valid(ToDomain(row));
	}

	public ApiKeyCreated Create(TagSet requiredTags, IReadOnlyList<string>? allowedKeyPrefixes, string description, DateTimeOffset at, string actor = "system")
	{
		ArgumentNullException.ThrowIfNull(requiredTags);
		ArgumentNullException.ThrowIfNull(description);
		ArgumentNullException.ThrowIfNull(actor);

		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.create-api-key");

		var plaintext = ApiKeyTokenGenerator.New();
		var hash = ApiKeyTokenGenerator.HashHex(plaintext);
		var prefix = ApiKeyTokenGenerator.Prefix(plaintext);
		var prefixesJson = allowedKeyPrefixes is null or { Count: 0 }
			? null
			: JsonSerializer.Serialize(allowedKeyPrefixes);
		var ts = at.ToUnixTimeMilliseconds();

		using var db = Open();
		using var tx = db.BeginTransaction();
		var id = Convert.ToInt64(db.InsertWithIdentity(new ApiKeyRow
		{
			TokenHash = hash,
			TokenPrefix = prefix,
			RequiredTagsJson = requiredTags.CanonicalJson,
			AllowedKeyPrefixes = prefixesJson,
			Description = description,
			UpdatedAt = ts,
			IsDeleted = 0,
		}));

		SqliteAuditLogStore.Append(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = AuditAction.Created.ToString(),
			EntityType = AuditEntityType.ApiKey.ToString(),
			TagSetJson = requiredTags.CanonicalJson,
			KeyPath = prefix,
			OldValue = null,
			NewValue = $"{description}|prefixes={prefixesJson ?? "null"}",
			OldHash = null,
			NewHash = hash,
		});
		tx.Commit();

		return new ApiKeyCreated(
			new ApiKeyInfo(id, prefix, requiredTags, allowedKeyPrefixes, description, at),
			plaintext);
	}

	public IReadOnlyList<ApiKeyInfo> ListActive()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-api-keys");
		using var db = Open();
		var rows = db.GetTable<ApiKeyRow>()
			.Where(r => r.IsDeleted == 0)
			.OrderBy(r => r.Description)
			.ToArray();
		return [.. rows.Select(r => new ApiKeyInfo(
			r.Id,
			r.TokenPrefix,
			TagSet.FromCanonicalJson(r.RequiredTagsJson),
			DeserializePrefixes(r.AllowedKeyPrefixes),
			r.Description,
			DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt)))];
	}

	public bool SoftDelete(long id, DateTimeOffset at, string actor = "system")
	{
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-api-key");
		using var db = Open();
		using var tx = db.BeginTransaction();
		var existing = db.GetTable<ApiKeyRow>().FirstOrDefault(r => r.Id == id && r.IsDeleted == 0);
		if (existing is null) return false;

		var ts = at.ToUnixTimeMilliseconds();
		db.GetTable<ApiKeyRow>()
			.Where(r => r.Id == id && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Set(r => r.UpdatedAt, ts)
			.Update();

		SqliteAuditLogStore.Append(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = AuditAction.Deleted.ToString(),
			EntityType = AuditEntityType.ApiKey.ToString(),
			TagSetJson = existing.RequiredTagsJson,
			KeyPath = existing.TokenPrefix,
			OldValue = $"{existing.Description}|prefixes={existing.AllowedKeyPrefixes ?? "null"}",
			NewValue = null,
			OldHash = existing.TokenHash,
			NewHash = null,
		});
		tx.Commit();
		return true;
	}

	static ApiKey ToDomain(ApiKeyRow r) => new()
	{
		Id = r.Id,
		TokenPrefix = r.TokenPrefix,
		TokenHash = r.TokenHash,
		RequiredTags = TagSet.FromCanonicalJson(r.RequiredTagsJson),
		AllowedKeyPrefixes = DeserializePrefixes(r.AllowedKeyPrefixes),
		Description = r.Description,
		UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt),
		IsDeleted = r.IsDeleted != 0,
	};

	static string[]? DeserializePrefixes(string? json) =>
		string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<string[]>(json);
}
