using Microsoft.Extensions.Options;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Storage;

// Integration tests — exercise real SQLite through linq2db with a fresh tmp DB per fact.
// Fixture pattern: each test gets its own directory (so parallel runs don't collide), and
// `Dispose` tears down the directory. SQLite connection-pooling is lazy, so closing our
// DataConnections (via `using` inside the store) is enough for the file to unlock.
public sealed class SqliteConfigStoreTests : IDisposable
{
	readonly string dir;
	readonly SqliteConfigStore store;

	public SqliteConfigStoreTests()
	{
		dir = Path.Combine(Path.GetTempPath(), $"yobaconf-test-{Guid.NewGuid():N}");
		var opts = Options.Create(new SqliteConfigStoreOptions { DataDirectory = dir });
		store = new SqliteConfigStore(opts);
	}

	public void Dispose()
	{
		// linq2db pools connections on the thread; force release so the file can be deleted.
		// `SqliteConnection.ClearAllPools` would be the hammer but pulling in Microsoft.Data.Sqlite
		// explicitly is overkill — fall back to best-effort deletion with a couple of retries.
		for (var i = 0; i < 3; i++)
		{
			try
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				Directory.Delete(dir, recursive: true);
				return;
			}
			catch (IOException)
			{
				Thread.Sleep(50);
			}
		}
	}

	[Fact]
	public void UpsertNode_ThenFindNode_RoundTripsContent()
	{
		var path = NodePath.ParseDb("app");
		store.UpsertNode(path, "name = yoba", DateTimeOffset.UnixEpoch);

		var got = store.FindNode(path);

		got.Should().NotBeNull();
		got!.Path.Should().Be(path);
		got.RawContent.Should().Be("name = yoba");
	}

	[Fact]
	public void FindNode_OnMissingPath_ReturnsNull()
	{
		store.FindNode(NodePath.ParseDb("nowhere")).Should().BeNull();
	}

	[Fact]
	public void UpsertNode_Twice_OverwritesExistingRow()
	{
		var path = NodePath.ParseDb("app");
		store.UpsertNode(path, "a = 1", DateTimeOffset.UnixEpoch);
		store.UpsertNode(path, "a = 2", DateTimeOffset.UnixEpoch);

		store.FindNode(path)!.RawContent.Should().Be("a = 2");
	}

	[Fact]
	public void SoftDeleteNode_HidesFromFind()
	{
		var path = NodePath.ParseDb("app");
		store.UpsertNode(path, "x = 1", DateTimeOffset.UnixEpoch);
		store.SoftDeleteNode(path);

		store.FindNode(path).Should().BeNull();
	}

	[Fact]
	public void UpsertAfterSoftDelete_ResurrectsTheNode()
	{
		var path = NodePath.ParseDb("app");
		store.UpsertNode(path, "original = 1", DateTimeOffset.UnixEpoch);
		store.SoftDeleteNode(path);
		store.UpsertNode(path, "revived = 2", DateTimeOffset.UnixEpoch);

		var got = store.FindNode(path);
		got.Should().NotBeNull();
		got!.RawContent.Should().Be("revived = 2");
	}

	[Fact]
	public void UpsertVariable_ThenFindVariables_ReturnsIt()
	{
		var scope = NodePath.ParseDb("app");
		store.UpsertVariable(scope, "db_host", "localhost", DateTimeOffset.UnixEpoch);

		var vars = store.FindVariables(scope);

		vars.Should().ContainSingle()
			.Which.Should().Match<Variable>(v =>
				v.Key == "db_host" && v.Value == "localhost" && v.ScopePath == scope);
	}

	[Fact]
	public void FindVariables_OtherScope_NotReturned()
	{
		store.UpsertVariable(NodePath.ParseDb("app"), "k", "v", DateTimeOffset.UnixEpoch);

		store.FindVariables(NodePath.ParseDb("other")).Should().BeEmpty();
	}

	[Fact]
	public void UpsertVariable_SameScopeAndKey_Overwrites()
	{
		var scope = NodePath.ParseDb("app");
		store.UpsertVariable(scope, "k", "first", DateTimeOffset.UnixEpoch);
		store.UpsertVariable(scope, "k", "second", DateTimeOffset.UnixEpoch);

		var vars = store.FindVariables(scope);
		vars.Should().ContainSingle()
			.Which.Value.Should().Be("second");
	}

	[Fact]
	public void SoftDeleteVariable_HidesFromFind()
	{
		var scope = NodePath.ParseDb("app");
		store.UpsertVariable(scope, "k", "v", DateTimeOffset.UnixEpoch);
		store.SoftDeleteVariable(scope, "k");

		store.FindVariables(scope).Should().BeEmpty();
	}

	[Fact]
	public void UpsertVariable_AfterSoftDelete_InsertsNewLiveRow()
	{
		// Partial unique index covers only live rows — a soft-deleted row + new live row at
		// the same (scope, key) must coexist.
		var scope = NodePath.ParseDb("app");
		store.UpsertVariable(scope, "k", "original", DateTimeOffset.UnixEpoch);
		store.SoftDeleteVariable(scope, "k");
		store.UpsertVariable(scope, "k", "revived", DateTimeOffset.UnixEpoch);

		var vars = store.FindVariables(scope);
		vars.Should().ContainSingle()
			.Which.Value.Should().Be("revived");
	}

	[Fact]
	public void UpsertSecret_ThenFindSecrets_ReturnsIt()
	{
		var scope = NodePath.ParseDb("app");
		store.UpsertSecret(scope, "api_key", [0x01, 0x02], [0x10], [0x20], "v1", DateTimeOffset.UnixEpoch);

		var secrets = store.FindSecrets(scope);

		secrets.Should().ContainSingle()
			.Which.Should().Match<Secret>(s =>
				s.Key == "api_key" && s.KeyVersion == "v1" && s.ScopePath == scope);
		secrets[0].EncryptedValue.Should().Equal([0x01, 0x02]);
	}

	[Fact]
	public void SoftDeleteSecret_HidesFromFind()
	{
		var scope = NodePath.ParseDb("app");
		store.UpsertSecret(scope, "api_key", [0x01], [0x02], [0x03], "v1", DateTimeOffset.UnixEpoch);
		store.SoftDeleteSecret(scope, "api_key");

		store.FindSecrets(scope).Should().BeEmpty();
	}

	[Fact]
	public void FullPipeline_EndToEnd_Through_SqliteStore()
	{
		// Seed the real SQLite store, then run ResolvePipeline over it — smoke-test that
		// the real store behaves equivalently to InMemoryConfigStore for the full §4 flow.
		store.UpsertNode(NodePath.ParseDb("project-a"), "log_level = info", DateTimeOffset.UnixEpoch);
		store.UpsertNode(NodePath.ParseDb("project-a/prod"), "db = ${db_host}\nenv = prod", DateTimeOffset.UnixEpoch);
		store.UpsertVariable(NodePath.ParseDb("project-a"), "db_host", "prod-db", DateTimeOffset.UnixEpoch);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("project-a/prod"), store);

		var json = System.Text.Json.Nodes.JsonNode.Parse(result.Json)!;
		json["db"]!.GetValue<string>().Should().Be("prod-db");
		json["env"]!.GetValue<string>().Should().Be("prod");
		json["db_host"]!.GetValue<string>().Should().Be("prod-db");
		result.ETag.Should().HaveLength(16);
	}

	[Fact]
	public void SchemaBootstrap_IsIdempotent_AcrossReopens()
	{
		// Close-reopen: the store re-runs `AllStatements`; CREATE IF NOT EXISTS must no-op.
		store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch);
		var opts = Options.Create(new SqliteConfigStoreOptions { DataDirectory = dir });
		var reopened = new SqliteConfigStore(opts);

		reopened.FindNode(NodePath.ParseDb("app"))!.RawContent.Should().Be("x = 1");
	}
}
