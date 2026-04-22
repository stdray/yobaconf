using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Bindings;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IBindingStore + IBindingStoreAdmin (tagged-v2). One file per instance.
// Schema applied on construction via SqliteSchema.AllStatements — idempotent, safe on
// every startup.
//
// Connection strategy: per-call `using var db = Open()`. SQLite's connection pooling
// plus WAL mode keep the short-lived-connection pattern cheap, and each call gets its
// own implicit transaction (linq2db wraps single commands).
//
// `FindMatching` does in-memory subset filtering — pet-scale (≤200 bindings per spec §6)
// makes the `json_each` subset predicate premature. If the observed row count grows into
// the thousands a follow-up in plan.md §perf can swap to SQL.
public sealed class SqliteBindingStore : IBindingStore, IBindingStoreAdmin
{
	readonly string dbPath;

	public SqliteBindingStore(IOptions<SqliteBindingStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.DataDirectory))
			throw new InvalidOperationException(
				"Storage:DataDirectory is empty. Set it in appsettings.json or env var " +
				"Storage__DataDirectory to an absolute path the process can write to.");

		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		using var db = Open();
		foreach (var stmt in SqliteSchema.AllStatements)
			db.Execute(stmt);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection(
			$"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		// WAL mode persists at the DB level so this PRAGMA is idempotent — the first
		// connection after file-create sets it, subsequent calls are a no-op read.
		db.Execute("PRAGMA journal_mode=WAL;");
		return db;
	}

	public Binding? FindById(long id)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-binding");
		using var db = Open();
		var row = db.GetTable<BindingRow>().FirstOrDefault(r => r.Id == id);
		return row is null ? null : ToDomain(row);
	}

	public IReadOnlyList<Binding> ListActive()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-bindings");
		using var db = Open();
		var rows = db.GetTable<BindingRow>()
			.Where(r => r.IsDeleted == 0)
			.OrderBy(r => r.TagSetJson).ThenBy(r => r.KeyPath)
			.ToArray();
		activity?.SetTag("yobaconf.bindings.count", rows.Length);
		return [.. rows.Select(ToDomain)];
	}

	public IReadOnlyList<Binding> FindMatching(IReadOnlyDictionary<string, string> tagVector)
	{
		ArgumentNullException.ThrowIfNull(tagVector);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-matching");
		activity?.SetTag("yobaconf.tag-vector.count", tagVector.Count);

		using var db = Open();
		var rows = db.GetTable<BindingRow>()
			.Where(r => r.IsDeleted == 0)
			.ToArray();

		var matched = new List<Binding>(rows.Length);
		foreach (var row in rows)
		{
			var domain = ToDomain(row);
			if (domain.TagSet.IsSubsetOf(tagVector))
				matched.Add(domain);
		}
		activity?.SetTag("yobaconf.matched.count", matched.Count);
		return matched;
	}

	public UpsertOutcome Upsert(Binding binding)
	{
		ArgumentNullException.ThrowIfNull(binding);
		Slug.RequireKeyPath(binding.KeyPath);

		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.upsert-binding");
		activity?.SetTag("yobaconf.tag-count", binding.TagSet.Count);
		activity?.SetTag("yobaconf.key-path", binding.KeyPath);

		using var db = Open();
		var tagSetJson = binding.TagSet.CanonicalJson;
		var ts = binding.UpdatedAt.ToUnixTimeMilliseconds();
		var contentHash = ComputeContentHash(binding);

		var existing = db.GetTable<BindingRow>()
			.FirstOrDefault(r => r.TagSetJson == tagSetJson && r.KeyPath == binding.KeyPath && r.IsDeleted == 0);

		if (existing is null)
		{
			var row = ToRow(binding, tagSetJson, contentHash, ts, id: 0);
			var newId = Convert.ToInt64(db.InsertWithIdentity(row));
			var inserted = binding with { Id = newId, ContentHash = contentHash };
			return new UpsertOutcome(inserted, OldHash: null);
		}

		var oldHash = existing.ContentHash;
		db.GetTable<BindingRow>()
			.Where(r => r.Id == existing.Id)
			.Set(r => r.ValuePlain, binding.ValuePlain)
			.Set(r => r.Ciphertext, binding.Ciphertext)
			.Set(r => r.Iv, binding.Iv)
			.Set(r => r.AuthTag, binding.AuthTag)
			.Set(r => r.KeyVersion, binding.KeyVersion)
			.Set(r => r.Kind, binding.Kind.ToString())
			.Set(r => r.ContentHash, contentHash)
			.Set(r => r.UpdatedAt, ts)
			.Update();

		var updated = binding with { Id = existing.Id, ContentHash = contentHash };
		return new UpsertOutcome(updated, OldHash: oldHash);
	}

	public bool SoftDelete(long id, DateTimeOffset at)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-binding");
		using var db = Open();
		var affected = db.GetTable<BindingRow>()
			.Where(r => r.Id == id && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Set(r => r.UpdatedAt, at.ToUnixTimeMilliseconds())
			.Update();
		return affected > 0;
	}

	static BindingRow ToRow(Binding b, string tagSetJson, string contentHash, long ts, long id) => new()
	{
		Id = id,
		TagSetJson = tagSetJson,
		TagCount = b.TagSet.Count,
		KeyPath = b.KeyPath,
		ValuePlain = b.ValuePlain,
		Ciphertext = b.Ciphertext,
		Iv = b.Iv,
		AuthTag = b.AuthTag,
		KeyVersion = b.KeyVersion,
		Kind = b.Kind.ToString(),
		ContentHash = contentHash,
		UpdatedAt = ts,
		IsDeleted = 0,
	};

	static Binding ToDomain(BindingRow r) => new()
	{
		Id = r.Id,
		TagSet = TagSet.FromCanonicalJson(r.TagSetJson),
		KeyPath = r.KeyPath,
		Kind = Enum.Parse<BindingKind>(r.Kind, ignoreCase: false),
		ValuePlain = r.ValuePlain,
		Ciphertext = r.Ciphertext,
		Iv = r.Iv,
		AuthTag = r.AuthTag,
		KeyVersion = r.KeyVersion,
		ContentHash = r.ContentHash,
		UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt),
		IsDeleted = r.IsDeleted != 0,
	};

	static string ComputeContentHash(Binding b)
	{
		// Plain: hash the JSON-encoded scalar text. Secret: hash the ciphertext bytes.
		// Both produce a stable identifier for optimistic locking + audit-diff without
		// ever touching plaintext of secrets.
		var input = b.Kind switch
		{
			BindingKind.Plain => Encoding.UTF8.GetBytes(b.ValuePlain ?? string.Empty),
			BindingKind.Secret => b.Ciphertext ?? [],
			_ => throw new InvalidOperationException($"Unknown binding kind '{b.Kind}'."),
		};
		return Convert.ToHexStringLower(SHA256.HashData(input));
	}
}
