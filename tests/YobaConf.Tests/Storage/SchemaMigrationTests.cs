using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Bindings;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Storage;

// Simulates the prod situation where a v1 path-tree deploy left its tables + an
// incompatible AuditLog in the persistent volume. First boot on v2 code must drop the v1
// artifacts (the DROP branch is gated on PRAGMA user_version < 2) and proceed to
// CREATE v2 schema without the "no such column: TagSetJson" crash that hit prod.
public sealed class SchemaMigrationTests
{
	static void SeedV1Tables(string dbPath)
	{
		using var db = SQLiteTools.CreateDataConnection(
			$"Data Source={dbPath};Cache=Shared;Pooling=True");
		db.Execute("PRAGMA journal_mode=WAL;");
		// v1 artefacts — table names + column shapes that used to live here.
		db.Execute(@"
			CREATE TABLE IF NOT EXISTS Nodes (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				Path TEXT NOT NULL UNIQUE,
				RawContent TEXT NOT NULL,
				ContentHash TEXT NOT NULL,
				UpdatedAt INTEGER NOT NULL,
				IsDeleted INTEGER NOT NULL DEFAULT 0);");
		db.Execute(@"
			CREATE TABLE IF NOT EXISTS Variables (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				Key TEXT NOT NULL,
				Value TEXT NOT NULL,
				ScopePath TEXT NOT NULL,
				ContentHash TEXT NOT NULL,
				UpdatedAt INTEGER NOT NULL,
				IsDeleted INTEGER NOT NULL DEFAULT 0);");
		db.Execute(@"
			CREATE TABLE IF NOT EXISTS Secrets (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				Key TEXT NOT NULL,
				EncryptedValue BLOB NOT NULL,
				Iv BLOB NOT NULL,
				AuthTag BLOB NOT NULL,
				KeyVersion TEXT NOT NULL,
				ScopePath TEXT NOT NULL,
				ContentHash TEXT NOT NULL,
				UpdatedAt INTEGER NOT NULL,
				IsDeleted INTEGER NOT NULL DEFAULT 0);");
		// v1 AuditLog — note the `Path` / `EntryKey` columns; no `TagSetJson`.
		db.Execute(@"
			CREATE TABLE IF NOT EXISTS AuditLog (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				At INTEGER NOT NULL,
				Actor TEXT NOT NULL,
				Action TEXT NOT NULL,
				EntityType TEXT NOT NULL,
				Path TEXT NOT NULL,
				EntryKey TEXT NULL,
				OldValue TEXT NULL,
				NewValue TEXT NULL,
				OldHash TEXT NULL,
				NewHash TEXT NULL);");
		// user_version stays at 0 (v1 never set it) — migration path gets triggered.
	}

	static IOptions<SqliteBindingStoreOptions> Opts(TempDb tmp) =>
		Options.Create(new SqliteBindingStoreOptions
		{
			DataDirectory = tmp.Directory,
			FileName = tmp.FileName,
		});

	[Fact]
	public void V1_Volume_Boots_Clean_On_V2_Code()
	{
		using var tmp = new TempDb();
		SeedV1Tables(tmp.Path);

		// Would crash with "no such column: TagSetJson" pre-migration.
		var store = new SqliteBindingStore(Opts(tmp));
		store.ListActive().Should().BeEmpty();

		// v1 tables must be gone; v2 Bindings must be usable.
		store.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "k",
			Kind = BindingKind.Plain,
			ValuePlain = "\"v\"",
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UnixEpoch,
		});
		store.ListActive().Should().ContainSingle();
	}

	[Fact]
	public void Migration_Stamps_UserVersion_Once()
	{
		// After first boot, user_version=2 → second boot must not re-run the DROP branch
		// (which would destroy AuditLog history). Emulate by: (1) seed v1, (2) boot v2,
		// insert an audit-producing mutation, (3) boot v2 again, confirm audit rows survive.
		using var tmp = new TempDb();
		SeedV1Tables(tmp.Path);

		var first = new SqliteBindingStore(Opts(tmp));
		first.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "k",
			Kind = BindingKind.Plain,
			ValuePlain = "\"v\"",
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UnixEpoch,
		}, actor: "alice");
		var auditFirst = new SqliteAuditLogStore(Opts(tmp));
		auditFirst.ListRecent(10).Should().ContainSingle();

		// Second boot — new stores on the same file. DROP must not re-fire.
		_ = new SqliteBindingStore(Opts(tmp));
		var auditSecond = new SqliteAuditLogStore(Opts(tmp));
		auditSecond.ListRecent(10).Should().ContainSingle("audit row survives second boot");
	}

	[Fact]
	public void Fresh_Db_Gets_V2_Schema_Directly()
	{
		using var tmp = new TempDb();
		// No v1 seed — pristine file.
		var store = new SqliteBindingStore(Opts(tmp));
		store.ListActive().Should().BeEmpty();
		store.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "k",
			Kind = BindingKind.Plain,
			ValuePlain = "\"v\"",
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UnixEpoch,
		});
		store.ListActive().Should().ContainSingle();
	}
}
