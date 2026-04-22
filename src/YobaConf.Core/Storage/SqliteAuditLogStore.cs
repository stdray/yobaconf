using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// Reader + internal appender for the AuditLog table. The reader surface is IAuditLogStore;
// the appender (InternalAppend) is called only from the other Sqlite stores. No public
// write API — pages never hit this directly (spec §3 invariant: storage is the single
// writer).
public sealed class SqliteAuditLogStore : IAuditLogStore
{
	readonly string dbPath;

	public SqliteAuditLogStore(IOptions<SqliteBindingStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.DataDirectory))
			throw new InvalidOperationException("Storage:DataDirectory is empty.");
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
		db.Execute("PRAGMA journal_mode=WAL;");
		return db;
	}

	public IReadOnlyList<AuditLogEntry> ListRecent(int limit)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-audit-recent");
		using var db = Open();
		var rows = db.GetTable<AuditLogRow>()
			.OrderByDescending(r => r.At)
			.ThenByDescending(r => r.Id)
			.Take(limit)
			.ToArray();
		return [.. rows.Select(ToDomain)];
	}

	public IReadOnlyList<AuditLogEntry> Query(AuditEntityType? entityType, string? actor, string? keyPathSubstring, int limit)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.query-audit");
		using var db = Open();
		IQueryable<AuditLogRow> q = db.GetTable<AuditLogRow>();
		if (entityType is not null)
		{
			var name = entityType.ToString()!;
			q = q.Where(r => r.EntityType == name);
		}
		if (!string.IsNullOrEmpty(actor))
			q = q.Where(r => r.Actor == actor);
		if (!string.IsNullOrEmpty(keyPathSubstring))
			q = q.Where(r => r.KeyPath != null && r.KeyPath.Contains(keyPathSubstring));
		return [.. q.OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Take(limit).ToArray().Select(ToDomain)];
	}

	public AuditLogEntry? FindById(long id)
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-audit");
		using var db = Open();
		var row = db.GetTable<AuditLogRow>().FirstOrDefault(r => r.Id == id);
		return row is null ? null : ToDomain(row);
	}

	// Internal append path — called from SqliteBindingStore / SqliteApiKeyStore /
	// SqliteUserStore inside the same open connection, no new one. Takes a DataConnection
	// so the caller's transaction wraps both the write and the audit entry atomically.
	internal static void Append(DataConnection db, AuditLogRow row) =>
		db.Insert(row);

	static AuditLogEntry ToDomain(AuditLogRow r) => new(
		Id: r.Id,
		At: DateTimeOffset.FromUnixTimeMilliseconds(r.At),
		Actor: r.Actor,
		Action: Enum.Parse<AuditAction>(r.Action, ignoreCase: false),
		EntityType: Enum.Parse<AuditEntityType>(r.EntityType, ignoreCase: false),
		TagSetJson: r.TagSetJson,
		KeyPath: r.KeyPath,
		OldValue: r.OldValue,
		NewValue: r.NewValue,
		OldHash: r.OldHash,
		NewHash: r.NewHash);
}
