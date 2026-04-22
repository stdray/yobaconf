using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IConfigStore + IConfigStoreAdmin + IAuditLogStore. One file per instance
// (spec §2). Schema applied on construction via SqliteSchema.AllStatements — idempotent
// CREATE IF NOT EXISTS, safe on every startup.
//
// Connection strategy: per-call `using var db = Open()` via SQLiteTools.CreateDataConnection.
// SQLite's internal connection pooling + WAL mode handle the short-lived pattern well.
//
// Phase B semantics:
//   * Every Upsert*/SoftDelete* returns UpsertOutcome (Inserted / Updated / Deleted / Conflict).
//   * When expectedHash is non-null, the write uses `WHERE ContentHash = @expected` — zero
//     rows affected means Conflict (caller re-fetches and shows merge UI).
//   * Every Inserted/Updated/Deleted outcome appends one AuditLog row in the same connection
//     (no explicit transaction — linq2db's per-call semantics keep it consistent for our
//     single-writer admin-UI scale).
//
// Phase C.5 tracing: every public method wraps its body in `sqlite.<op>` activity.
public sealed class SqliteConfigStore : IConfigStore, IConfigStoreAdmin, IAuditLogStore
{
	readonly string dbPath;

	public SqliteConfigStore(IOptions<SqliteConfigStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;

		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		using var db = Open();
		foreach (var stmt in SqliteSchema.AllStatements)
			db.Execute(stmt);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection($"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		db.Execute("PRAGMA journal_mode=WAL;");
		return db;
	}

	public HoconNode? FindNode(NodePath path)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-node");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		var row = db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical && r.IsDeleted == 0)
			.FirstOrDefault();
		return row is null
			? null
			: new HoconNode(NodePath.ParseDb(row.Path), row.RawContent, FromUnixMs(row.UpdatedAt), row.ContentHash);
	}

	public IReadOnlyList<NodePath> ListNodePaths()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-node-paths");
		using var db = Open();
		var paths = db.GetTable<NodeRow>()
			.Where(r => r.IsDeleted == 0)
			.OrderBy(r => r.Path)
			.Select(r => r.Path)
			.ToArray();
		activity?.SetTag("yobaconf.nodes.count", paths.Length);
		return [.. paths.Select(NodePath.ParseDb)];
	}

	public IReadOnlyList<Variable> FindVariables(NodePath scope)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-variables");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var rows = db.GetTable<VariableRow>()
			.Where(r => r.ScopePath == canonical && r.IsDeleted == 0)
			.ToArray();
		return [.. rows.Select(ToDomain)];
	}

	public IReadOnlyList<Secret> FindSecrets(NodePath scope)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-secrets");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var rows = db.GetTable<SecretRow>()
			.Where(r => r.ScopePath == canonical && r.IsDeleted == 0)
			.ToArray();
		return [.. rows.Select(ToDomain)];
	}

	public UpsertOutcome UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(rawContent);
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-node");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		var hash = Sha256Hex(rawContent);
		var ts = updatedAt.ToUnixTimeMilliseconds();

		var existing = db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical)
			.FirstOrDefault();

		if (existing is null || existing.IsDeleted == 1)
		{
			// Insert path (either fresh row or revive soft-deleted). Optimistic-lock check
			// makes no sense here — caller expected an existing live row but there isn't one.
			if (expectedHash is not null)
				return UpsertOutcome.Conflict;

			if (existing is null)
			{
				db.Insert(new NodeRow
				{
					Path = canonical,
					RawContent = rawContent,
					ContentHash = hash,
					UpdatedAt = ts,
					IsDeleted = 0,
				});
			}
			else
			{
				db.GetTable<NodeRow>()
					.Where(r => r.Path == canonical)
					.Set(r => r.RawContent, rawContent)
					.Set(r => r.ContentHash, hash)
					.Set(r => r.UpdatedAt, ts)
					.Set(r => r.IsDeleted, 0)
					.Update();
			}

			AppendAudit(db, new AuditLogRow
			{
				At = ts,
				Actor = actor,
				Action = nameof(AuditAction.Created),
				EntityType = nameof(AuditEntityType.Node),
				Path = canonical,
				OldValue = null,
				NewValue = rawContent,
				OldHash = null,
				NewHash = hash,
			});
			return UpsertOutcome.Inserted;
		}

		// Existing live row. Optimistic-lock check.
		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		// No-op short-circuit: if the new content hash equals the current one, skip the
		// audit row. Keeps save-without-change quiet.
		if (string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
			return UpsertOutcome.Updated;

		db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical)
			.Set(r => r.RawContent, rawContent)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Update();

		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Updated),
			EntityType = nameof(AuditEntityType.Node),
			Path = canonical,
			OldValue = existing.RawContent,
			NewValue = rawContent,
			OldHash = existing.ContentHash,
			NewHash = hash,
		});
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-variable");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var hash = Sha256Hex(value);
		var ts = updatedAt.ToUnixTimeMilliseconds();

		var existing = db.GetTable<VariableRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.FirstOrDefault();

		if (existing is null)
		{
			if (expectedHash is not null)
				return UpsertOutcome.Conflict;

			db.Insert(new VariableRow
			{
				Key = key,
				Value = value,
				ScopePath = canonical,
				ContentHash = hash,
				UpdatedAt = ts,
				IsDeleted = 0,
			});
			AppendAudit(db, new AuditLogRow
			{
				At = ts,
				Actor = actor,
				Action = nameof(AuditAction.Created),
				EntityType = nameof(AuditEntityType.Variable),
				Path = canonical,
				EntryKey = key,
				OldValue = null,
				NewValue = value,
				OldHash = null,
				NewHash = hash,
			});
			return UpsertOutcome.Inserted;
		}

		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		if (string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
			return UpsertOutcome.Updated;

		db.GetTable<VariableRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.Value, value)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Update();
		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Updated),
			EntityType = nameof(AuditEntityType.Variable),
			Path = canonical,
			EntryKey = key,
			OldValue = existing.Value,
			NewValue = value,
			OldHash = existing.ContentHash,
			NewHash = hash,
		});
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(encryptedValue);
		ArgumentNullException.ThrowIfNull(iv);
		ArgumentNullException.ThrowIfNull(authTag);
		ArgumentNullException.ThrowIfNull(keyVersion);
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-secret");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var hash = Sha256HexOfBytes(encryptedValue);
		var ts = updatedAt.ToUnixTimeMilliseconds();
		var bundle = SerializeSecretBundle(encryptedValue, iv, authTag, keyVersion);

		var existing = db.GetTable<SecretRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.FirstOrDefault();

		if (existing is null)
		{
			if (expectedHash is not null)
				return UpsertOutcome.Conflict;

			db.Insert(new SecretRow
			{
				Key = key,
				EncryptedValue = encryptedValue,
				Iv = iv,
				AuthTag = authTag,
				KeyVersion = keyVersion,
				ScopePath = canonical,
				ContentHash = hash,
				UpdatedAt = ts,
				IsDeleted = 0,
			});
			AppendAudit(db, new AuditLogRow
			{
				At = ts,
				Actor = actor,
				Action = nameof(AuditAction.Created),
				EntityType = nameof(AuditEntityType.Secret),
				Path = canonical,
				EntryKey = key,
				OldValue = null,
				NewValue = bundle,
				OldHash = null,
				NewHash = hash,
			});
			return UpsertOutcome.Inserted;
		}

		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		if (string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
			return UpsertOutcome.Updated;

		var oldBundle = SerializeSecretBundle(existing.EncryptedValue, existing.Iv, existing.AuthTag, existing.KeyVersion);
		db.GetTable<SecretRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.EncryptedValue, encryptedValue)
			.Set(r => r.Iv, iv)
			.Set(r => r.AuthTag, authTag)
			.Set(r => r.KeyVersion, keyVersion)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Update();
		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Updated),
			EntityType = nameof(AuditEntityType.Secret),
			Path = canonical,
			EntryKey = key,
			OldValue = oldBundle,
			NewValue = bundle,
			OldHash = existing.ContentHash,
			NewHash = hash,
		});
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteNode(NodePath path, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-node");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		var existing = db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical && r.IsDeleted == 0)
			.FirstOrDefault();
		if (existing is null)
			return UpsertOutcome.Conflict;
		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		db.GetTable<NodeRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.IsDeleted, 1)
			.Update();
		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Deleted),
			EntityType = nameof(AuditEntityType.Node),
			Path = canonical,
			OldValue = existing.RawContent,
			NewValue = null,
			OldHash = existing.ContentHash,
			NewHash = null,
		});
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteVariable(NodePath scope, string key, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-variable");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var existing = db.GetTable<VariableRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.FirstOrDefault();
		if (existing is null)
			return UpsertOutcome.Conflict;
		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		db.GetTable<VariableRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.IsDeleted, 1)
			.Update();
		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Deleted),
			EntityType = nameof(AuditEntityType.Variable),
			Path = canonical,
			EntryKey = key,
			OldValue = existing.Value,
			NewValue = null,
			OldHash = existing.ContentHash,
			NewHash = null,
		});
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteSecret(NodePath scope, string key, string actor = "system", string? expectedHash = null)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-secret");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var existing = db.GetTable<SecretRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.FirstOrDefault();
		if (existing is null)
			return UpsertOutcome.Conflict;
		if (expectedHash is not null && !string.Equals(existing.ContentHash, expectedHash, StringComparison.Ordinal))
			return UpsertOutcome.Conflict;

		var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var oldBundle = SerializeSecretBundle(existing.EncryptedValue, existing.Iv, existing.AuthTag, existing.KeyVersion);
		db.GetTable<SecretRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.IsDeleted, 1)
			.Update();
		AppendAudit(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = nameof(AuditAction.Deleted),
			EntityType = nameof(AuditEntityType.Secret),
			Path = canonical,
			EntryKey = key,
			OldValue = oldBundle,
			NewValue = null,
			OldHash = existing.ContentHash,
			NewHash = null,
		});
		return UpsertOutcome.Updated;
	}

	public IReadOnlyList<AuditEntry> FindByPath(NodePath path, bool includeDescendants, int skip, int take)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.audit-find-by-path");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		var query = db.GetTable<AuditLogRow>().AsQueryable();
		if (includeDescendants)
		{
			var prefix = canonical.Length == 0 ? string.Empty : canonical + "/";
			query = query.Where(r => r.Path == canonical || r.Path.StartsWith(prefix));
		}
		else
		{
			query = query.Where(r => r.Path == canonical);
		}
		var rows = query
			.OrderByDescending(r => r.At)
			.ThenByDescending(r => r.Id)
			.Skip(skip)
			.Take(take)
			.ToArray();
		return [.. rows.Select(ToDomain)];
	}

	public AuditEntry? FindById(long id)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.audit-find-by-id");
		using var db = Open();
		var row = db.GetTable<AuditLogRow>().Where(r => r.Id == id).FirstOrDefault();
		return row is null ? null : ToDomain(row);
	}

	// --- helpers ---

	static void AppendAudit(DataConnection db, AuditLogRow row) => db.Insert(row);

	// Secret bundle serialization — base64(ciphertext) | base64(iv) | base64(authTag) | keyVersion.
	// Pipe separator chosen because base64 alphabet excludes `|`. Round-trips losslessly.
	static string SerializeSecretBundle(byte[] ciphertext, byte[] iv, byte[] authTag, string keyVersion) =>
		$"{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(iv)}|{Convert.ToBase64String(authTag)}|{keyVersion}";

	// Exposed for rollback handlers — parse a stored bundle back into its four fields.
	public static bool TryDeserializeSecretBundle(string bundle, out byte[] ciphertext, out byte[] iv, out byte[] authTag, out string keyVersion)
	{
		ciphertext = iv = authTag = []; keyVersion = string.Empty;
		if (string.IsNullOrEmpty(bundle)) return false;
		var parts = bundle.Split('|');
		if (parts.Length != 4) return false;
		try
		{
			ciphertext = Convert.FromBase64String(parts[0]);
			iv = Convert.FromBase64String(parts[1]);
			authTag = Convert.FromBase64String(parts[2]);
			keyVersion = parts[3];
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	static Variable ToDomain(VariableRow r) => new(
		r.Key,
		r.Value,
		NodePath.ParseDb(r.ScopePath),
		FromUnixMs(r.UpdatedAt),
		r.IsDeleted != 0,
		r.ContentHash);

	static Secret ToDomain(SecretRow r) => new(
		r.Key,
		r.EncryptedValue,
		r.Iv,
		r.AuthTag,
		r.KeyVersion,
		NodePath.ParseDb(r.ScopePath),
		FromUnixMs(r.UpdatedAt),
		r.IsDeleted != 0,
		r.ContentHash);

	static AuditEntry ToDomain(AuditLogRow r) => new(
		r.Id,
		DateTimeOffset.FromUnixTimeMilliseconds(r.At),
		r.Actor,
		Enum.Parse<AuditAction>(r.Action, ignoreCase: true),
		Enum.Parse<AuditEntityType>(r.EntityType, ignoreCase: true),
		NodePath.ParseDb(r.Path),
		r.EntryKey,
		r.OldValue,
		r.NewValue,
		r.OldHash,
		r.NewHash);

	static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

	static string Sha256Hex(string s) => Sha256HexOfBytes(Encoding.UTF8.GetBytes(s));

#pragma warning disable CA1308 // ContentHash hex is cosmetic, lowercase for readability
	static string Sha256HexOfBytes(byte[] bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308
}
