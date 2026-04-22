using Microsoft.Extensions.Options;
using YobaConf.Core;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Storage;

// Phase B: optimistic locking + append-only audit trail. Exercises SqliteConfigStore
// directly (not through InMemoryConfigStore — the SQLite round-trip is the thing that's
// actually going to prod).
public sealed class AuditLogTests : IDisposable
{
	readonly string _tempDir;
	readonly SqliteConfigStore _store;

	public AuditLogTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobaconf-audit-" + Guid.NewGuid().ToString("N")[..8]);
		_store = new SqliteConfigStore(Options.Create(new SqliteConfigStoreOptions { DataDirectory = _tempDir }));
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
	}

	// --- Upsert outcomes ---

	[Fact]
	public void Insert_On_New_Node_Returns_Inserted()
	{
		var outcome = _store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		outcome.Should().Be(UpsertOutcome.Inserted);
	}

	[Fact]
	public void Update_On_Existing_Node_WithoutHash_Returns_Updated()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		var outcome = _store.UpsertNode(NodePath.ParseDb("app"), "x = 2", DateTimeOffset.UnixEpoch, "tester");
		outcome.Should().Be(UpsertOutcome.Updated);
	}

	[Fact]
	public void Update_With_ExpectedHash_Match_Returns_Updated()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		var current = _store.FindNode(NodePath.ParseDb("app"));
		current.Should().NotBeNull();
		var outcome = _store.UpsertNode(NodePath.ParseDb("app"), "x = 2", DateTimeOffset.UnixEpoch, "tester", expectedHash: current!.ContentHash);
		outcome.Should().Be(UpsertOutcome.Updated);
	}

	[Fact]
	public void Update_With_ExpectedHash_Mismatch_Returns_Conflict()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		var outcome = _store.UpsertNode(NodePath.ParseDb("app"), "x = 2", DateTimeOffset.UnixEpoch, "tester", expectedHash: "deadbeef");
		outcome.Should().Be(UpsertOutcome.Conflict);
		// Conflict must NOT mutate state.
		var node = _store.FindNode(NodePath.ParseDb("app"));
		node!.RawContent.Should().Be("x = 1");
	}

	[Fact]
	public void Insert_With_ExpectedHash_Returns_Conflict()
	{
		// Caller expected an existing row but there isn't one.
		var outcome = _store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester", expectedHash: "someHash");
		outcome.Should().Be(UpsertOutcome.Conflict);
		_store.FindNode(NodePath.ParseDb("app")).Should().BeNull();
	}

	[Fact]
	public void SoftDelete_With_ExpectedHash_Mismatch_Returns_Conflict()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		var outcome = _store.SoftDeleteNode(NodePath.ParseDb("app"), "tester", expectedHash: "wrong");
		outcome.Should().Be(UpsertOutcome.Conflict);
		_store.FindNode(NodePath.ParseDb("app")).Should().NotBeNull();
	}

	[Fact]
	public void SoftDelete_NonExistent_Returns_Conflict()
	{
		var outcome = _store.SoftDeleteNode(NodePath.ParseDb("missing"), "tester");
		outcome.Should().Be(UpsertOutcome.Conflict);
	}

	[Fact]
	public void NoOp_Save_Same_Content_Returns_Updated_Without_Audit()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit.Should().HaveCount(1, "second call was a no-op; only the Created entry should exist");
	}

	// --- Audit trail ---

	[Fact]
	public void Insert_Appends_Created_Entry()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "name = yoba", DateTimeOffset.UnixEpoch, "alice");
		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit.Should().HaveCount(1);
		audit[0].Action.Should().Be(AuditAction.Created);
		audit[0].EntityType.Should().Be(AuditEntityType.Node);
		audit[0].Actor.Should().Be("alice");
		audit[0].OldValue.Should().BeNull();
		audit[0].NewValue.Should().Be("name = yoba");
	}

	[Fact]
	public void Update_Appends_Updated_Entry_WithOldAndNew()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "alice");
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 2", DateTimeOffset.UnixEpoch, "bob");

		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit.Should().HaveCount(2);
		// Newest-first ordering.
		audit[0].Action.Should().Be(AuditAction.Updated);
		audit[0].Actor.Should().Be("bob");
		audit[0].OldValue.Should().Be("x = 1");
		audit[0].NewValue.Should().Be("x = 2");
		audit[1].Action.Should().Be(AuditAction.Created);
		audit[1].Actor.Should().Be("alice");
	}

	[Fact]
	public void SoftDelete_Appends_Deleted_Entry()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "alice");
		_store.SoftDeleteNode(NodePath.ParseDb("app"), "bob");

		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit[0].Action.Should().Be(AuditAction.Deleted);
		audit[0].Actor.Should().Be("bob");
		audit[0].OldValue.Should().Be("x = 1");
		audit[0].NewValue.Should().BeNull();
	}

	[Fact]
	public void Variable_Crud_LandsIn_AuditLog_WithKey()
	{
		_store.UpsertVariable(NodePath.ParseDb("app"), "db_host", "localhost", DateTimeOffset.UnixEpoch, "alice");
		_store.UpsertVariable(NodePath.ParseDb("app"), "db_host", "prod-db", DateTimeOffset.UnixEpoch, "bob");
		_store.SoftDeleteVariable(NodePath.ParseDb("app"), "db_host", "carol");

		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit.Should().HaveCount(3);
		foreach (var e in audit)
		{
			e.EntityType.Should().Be(AuditEntityType.Variable);
			e.Key.Should().Be("db_host");
		}
	}

	[Fact]
	public void Secret_Create_Then_Update_LandsIn_AuditLog_WithBundleEncoded()
	{
		var cipher1 = new byte[] { 1, 2, 3 };
		var cipher2 = new byte[] { 4, 5, 6 };
		var iv = new byte[] { 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };
		var tag = new byte[] { 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35 };

		_store.UpsertSecret(NodePath.ParseDb("app"), "api_key", cipher1, iv, tag, "v1", DateTimeOffset.UnixEpoch, "alice");
		_store.UpsertSecret(NodePath.ParseDb("app"), "api_key", cipher2, iv, tag, "v1", DateTimeOffset.UnixEpoch, "bob");

		var audit = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		audit.Should().HaveCount(2);
		// Newest-first: Updated row carries old + new bundles.
		audit[0].Action.Should().Be(AuditAction.Updated);
		audit[0].EntityType.Should().Be(AuditEntityType.Secret);
		audit[0].OldValue.Should().NotBeNullOrEmpty();
		audit[0].NewValue.Should().NotBeNullOrEmpty();
		audit[0].OldValue.Should().NotBe(audit[0].NewValue);

		// Roundtrip the serialized bundle.
		var ok = SqliteConfigStore.TryDeserializeSecretBundle(audit[0].NewValue!, out var c, out _, out _, out var kv);
		ok.Should().BeTrue();
		c.Should().Equal(cipher2);
		kv.Should().Be("v1");
	}

	[Fact]
	public void FindByPath_WithDescendants_Includes_Children()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "tester");
		_store.UpsertNode(NodePath.ParseDb("app/prod"), "y = 2", DateTimeOffset.UnixEpoch, "tester");
		_store.UpsertNode(NodePath.ParseDb("app/prod/eu"), "z = 3", DateTimeOffset.UnixEpoch, "tester");
		_store.UpsertNode(NodePath.ParseDb("other"), "q = 9", DateTimeOffset.UnixEpoch, "tester");

		var withDesc = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: true, skip: 0, take: 50);
		withDesc.Should().HaveCount(3, "app + app/prod + app/prod/eu");

		var exact = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50);
		exact.Should().HaveCount(1);
	}

	[Fact]
	public void FindById_Round_Trips_Entry()
	{
		_store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch, "alice");
		var row = _store.FindByPath(NodePath.ParseDb("app"), includeDescendants: false, skip: 0, take: 50)[0];

		var fetched = _store.FindById(row.Id);
		fetched.Should().NotBeNull();
		fetched!.Actor.Should().Be("alice");
		fetched.NewValue.Should().Be("x = 1");
	}
}
