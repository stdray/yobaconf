using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Storage;

public sealed class SqliteTagVocabularyStoreTests
{
	static IOptions<SqliteBindingStoreOptions> Opts(TempDb tmp) =>
		Options.Create(new SqliteBindingStoreOptions
		{
			DataDirectory = tmp.Directory,
			FileName = tmp.FileName,
		});

	[Fact]
	public void Create_KeyOnly_RoundTrips()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		var entry = store.Create("env", value: null, description: "deployment env", priority: 0, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

		entry.Id.Should().BeGreaterThan(0);
		entry.Key.Should().Be("env");
		entry.Value.Should().BeNull();
		entry.Description.Should().Be("deployment env");
		entry.Priority.Should().Be(0);

		store.ListActive().Should().ContainSingle();
		store.DistinctKeys().Should().ContainSingle().Which.Should().Be("env");
	}

	[Fact]
	public void Create_KeyValue_RoundTrips_And_Dedupes_Keys()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		store.Create("env", "prod", description: null, priority: 0, DateTimeOffset.UnixEpoch);
		store.Create("env", "staging", description: null, priority: 0, DateTimeOffset.UnixEpoch);
		store.Create("project", "yobapub", description: null, priority: 0, DateTimeOffset.UnixEpoch);

		store.ListActive().Should().HaveCount(3);
		store.DistinctKeys().Should().BeEquivalentTo(["env", "project"]);
	}

	[Fact]
	public void Create_Rejects_Duplicate_KeyValue_Pair()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		store.Create("env", "prod", null, 0, DateTimeOffset.UnixEpoch);

		var act = () => store.Create("env", "prod", null, 0, DateTimeOffset.UnixEpoch);
		act.Should().Throw<InvalidOperationException>().WithMessage("*already declared*");
	}

	[Fact]
	public void Create_Rejects_Duplicate_KeyOnly_Declaration()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		store.Create("env", null, null, 0, DateTimeOffset.UnixEpoch);

		var act = () => store.Create("env", null, null, 0, DateTimeOffset.UnixEpoch);
		act.Should().Throw<InvalidOperationException>().WithMessage("*already declared*");
	}

	[Fact]
	public void SoftDelete_Removes_From_ListActive()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		var keep = store.Create("env", "prod", null, 0, DateTimeOffset.UnixEpoch);
		var drop = store.Create("env", "staging", null, 0, DateTimeOffset.UnixEpoch);

		store.SoftDelete(drop.Id, DateTimeOffset.UnixEpoch).Should().BeTrue();
		store.ListActive().Select(e => e.Id).Should().ContainSingle().Which.Should().Be(keep.Id);
		store.DistinctKeys().Should().ContainSingle().Which.Should().Be("env");
	}

	[Fact]
	public void SoftDelete_Returns_False_For_Unknown_Id() =>
		TempDb_DoWithStore((tmp, store) =>
			store.SoftDelete(id: 9999, DateTimeOffset.UnixEpoch).Should().BeFalse());

	[Fact]
	public void Create_Appends_Audit_Row()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));
		var audit = new SqliteAuditLogStore(Opts(tmp));

		store.Create("env", "prod", "deployment env", 0, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), actor: "alice");

		var rows = audit.ListRecent(10);
		rows.Should().ContainSingle();
		rows[0].EntityType.Should().Be(AuditEntityType.TagVocabulary);
		rows[0].Action.Should().Be(AuditAction.Created);
		rows[0].KeyPath.Should().Be("env");
		rows[0].NewValue.Should().Contain("prod");
		rows[0].Actor.Should().Be("alice");
	}

	[Fact]
	public void SoftDelete_Appends_Audit_Row()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));
		var audit = new SqliteAuditLogStore(Opts(tmp));

		var entry = store.Create("env", "prod", null, 0, DateTimeOffset.UnixEpoch, actor: "alice");
		store.SoftDelete(entry.Id, DateTimeOffset.UnixEpoch, actor: "bob");

		var rows = audit.ListRecent(10);
		rows.Should().HaveCount(2);
		var del = rows.Single(r => r.Action == AuditAction.Deleted);
		del.EntityType.Should().Be(AuditEntityType.TagVocabulary);
		del.KeyPath.Should().Be("env");
		del.Actor.Should().Be("bob");
	}

	[Fact]
	public void Fresh_Db_Reaches_Schema_V4()
	{
		using var tmp = new TempDb();
		_ = new SqliteTagVocabularyStore(Opts(tmp));

		// A reboot on a now-v3 DB must not re-trigger any migration branch.
		_ = new SqliteTagVocabularyStore(Opts(tmp));
		new SqliteTagVocabularyStore(Opts(tmp))
			.ListActive().Should().BeEmpty();
	}

	[Fact]
	public void Priority_RoundTrips()
	{
		using var tmp = new TempDb();
		var store = new SqliteTagVocabularyStore(Opts(tmp));

		var entry = store.Create("tier", "gold", "golden tier", priority: 42, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

		entry.Priority.Should().Be(42);

		var list = store.ListActive();
		list.Should().ContainSingle();
		list[0].Priority.Should().Be(42);
	}

	static void TempDb_DoWithStore(Action<TempDb, SqliteTagVocabularyStore> body)
	{
		using var tmp = new TempDb();
		body(tmp, new SqliteTagVocabularyStore(Opts(tmp)));
	}
}
