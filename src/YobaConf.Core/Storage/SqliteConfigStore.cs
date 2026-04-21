using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IConfigStore. One file per instance (spec §2 — yobaconf doesn't split by
// workspace the way yobalog does). Schema applied on construction via
// `SqliteSchema.AllStatements` — idempotent CREATE IF NOT EXISTS, safe on every startup.
//
// Connection strategy: per-call `using var db = Open()` via SQLiteTools.CreateDataConnection.
// SQLite's internal connection pooling + WAL mode handle the short-lived pattern well; no
// long-held state in this class. If benchmarks ever show this is a bottleneck, swap to
// a pooled DataContext — all read paths here are structured to allow that change without
// API fallout.
//
// Phase C.5 tracing: every public I/O method wraps its body in `sqlite.<op>` activity so
// yobalog's waterfall shows per-DB-call duration as a child of yobaconf.resolve /
// yobaconf.fallthrough-lookup / yobaconf.variables-resolve. Span creation is a no-op when
// no listener is attached (unit tests, OpenTelemetry:Enabled=false).
public sealed class SqliteConfigStore : IConfigStore, IConfigStoreAdmin
{
	readonly string dbPath;

	public SqliteConfigStore(IOptions<SqliteConfigStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;

		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		// Schema bootstrap. WAL-mode + foreign_keys pragmas applied per connection so the
		// whole instance lifetime benefits; applying here once would be lost on the next
		// fresh connection.
		using var db = Open();
		foreach (var stmt in SqliteSchema.AllStatements)
			db.Execute(stmt);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection($"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		// WAL gives concurrent readers while a writer commits — relevant as soon as the
		// admin UI and API-side reads coexist. Safe to set on every connection: SQLite
		// treats it as a no-op if already in WAL mode.
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
			: new HoconNode(NodePath.ParseDb(row.Path), row.RawContent, FromUnixMs(row.UpdatedAt));
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

	// Admin / seed API. Not part of IConfigStore (resolver side is read-only) but exposed
	// here for the Phase A bootstrap flow and the paste-import UI. Write paths compute
	// ContentHash inside the store so callers can't accidentally pass an inconsistent hash.

	public void UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt)
	{
		ArgumentNullException.ThrowIfNull(rawContent);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-node");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		var hash = Sha256Hex(rawContent);
		var ts = updatedAt.ToUnixTimeMilliseconds();

		// Try-update-then-insert on Path (the UNIQUE column). Revives a soft-deleted row if
		// one existed at the path — `IsDeleted` goes back to 0. Two-round-trip pattern is
		// fine for MVP (single-admin, no concurrent writes); `INSERT ... ON CONFLICT DO UPDATE`
		// UPSERT would cut to one round-trip if ever benchmarks show this is hot.
		var updated = db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical)
			.Set(r => r.RawContent, rawContent)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Set(r => r.IsDeleted, 0)
			.Update();
		if (updated == 0)
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
	}

	public void UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-variable");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var hash = Sha256Hex(value);
		var ts = updatedAt.ToUnixTimeMilliseconds();

		var updated = db.GetTable<VariableRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.Set(r => r.Value, value)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Update();
		if (updated == 0)
		{
			db.Insert(new VariableRow
			{
				Key = key,
				Value = value,
				ScopePath = canonical,
				ContentHash = hash,
				UpdatedAt = ts,
				IsDeleted = 0,
			});
		}
	}

	public void UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(encryptedValue);
		ArgumentNullException.ThrowIfNull(iv);
		ArgumentNullException.ThrowIfNull(authTag);
		ArgumentNullException.ThrowIfNull(keyVersion);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-secret");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		var hash = Sha256HexOfBytes(encryptedValue);
		var ts = updatedAt.ToUnixTimeMilliseconds();

		var updated = db.GetTable<SecretRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.Set(r => r.EncryptedValue, encryptedValue)
			.Set(r => r.Iv, iv)
			.Set(r => r.AuthTag, authTag)
			.Set(r => r.KeyVersion, keyVersion)
			.Set(r => r.ContentHash, hash)
			.Set(r => r.UpdatedAt, ts)
			.Update();
		if (updated == 0)
		{
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
		}
	}

	public void SoftDeleteNode(NodePath path)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-node");
		activity?.SetTag("yobaconf.path", path.ToDbPath());
		using var db = Open();
		var canonical = path.ToDbPath();
		_ = db.GetTable<NodeRow>()
			.Where(r => r.Path == canonical && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Update();
	}

	public void SoftDeleteVariable(NodePath scope, string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-variable");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		_ = db.GetTable<VariableRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Update();
	}

	public void SoftDeleteSecret(NodePath scope, string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-secret");
		activity?.SetTag("yobaconf.path", scope.ToDbPath());
		using var db = Open();
		var canonical = scope.ToDbPath();
		_ = db.GetTable<SecretRow>()
			.Where(r => r.ScopePath == canonical && r.Key == key && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Update();
	}

	static Variable ToDomain(VariableRow r) => new(
		r.Key,
		r.Value,
		NodePath.ParseDb(r.ScopePath),
		FromUnixMs(r.UpdatedAt),
		r.IsDeleted != 0);

	static Secret ToDomain(SecretRow r) => new(
		r.Key,
		r.EncryptedValue,
		r.Iv,
		r.AuthTag,
		r.KeyVersion,
		NodePath.ParseDb(r.ScopePath),
		FromUnixMs(r.UpdatedAt),
		r.IsDeleted != 0);

	static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

	static string Sha256Hex(string s) => Sha256HexOfBytes(Encoding.UTF8.GetBytes(s));

#pragma warning disable CA1308 // ContentHash hex is cosmetic, lowercase for readability
	static string Sha256HexOfBytes(byte[] bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308
}
