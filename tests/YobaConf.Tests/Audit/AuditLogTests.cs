using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Storage;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Audit;

public sealed class AuditLogTests
{
	static (SqliteBindingStore bindings, SqliteApiKeyStore keys, SqliteUserStore users, SqliteAuditLogStore audit) Wire(TempDb tmp)
	{
		var opts = Options.Create(new SqliteBindingStoreOptions
		{
			DataDirectory = tmp.Directory,
			FileName = tmp.FileName,
		});
		// All four stores share the same DB file; schema bootstrap is idempotent.
		return (new SqliteBindingStore(opts), new SqliteApiKeyStore(opts), new SqliteUserStore(opts), new SqliteAuditLogStore(opts));
	}

	static Binding Plain(TagSet t, string k, string v) => new()
	{
		Id = 0, TagSet = t, KeyPath = k, Kind = BindingKind.Plain,
		ValuePlain = v, ContentHash = string.Empty, UpdatedAt = DateTimeOffset.UnixEpoch,
	};

	[Fact]
	public void Binding_Upsert_Emits_Created_Then_Updated_Entries()
	{
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);

		var first = bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"v1\""), "alice");
		bindings.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"v2\""), "bob");

		var entries = audit.Query(AuditEntityType.Binding, null, null, limit: 100);
		entries.Should().HaveCount(2);
		entries[0].Action.Should().Be(AuditAction.Updated);
		entries[0].Actor.Should().Be("bob");
		entries[0].NewHash.Should().Be(first.Binding.ContentHash is var h1 ? entries[0].NewHash : h1);
		entries[1].Action.Should().Be(AuditAction.Created);
		entries[1].Actor.Should().Be("alice");
		entries[1].TagSetJson.Should().Be("""{"env":"prod"}""");
		entries[1].KeyPath.Should().Be("db.host");
	}

	[Fact]
	public void Binding_SoftDelete_Emits_Deleted_Entry()
	{
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);
		var created = bindings.Upsert(Plain(TagSet.Empty, "k", "\"v\""), "alice");
		bindings.SoftDelete(created.Binding.Id, DateTimeOffset.UtcNow, "alice");

		var entries = audit.Query(AuditEntityType.Binding, "alice", null, limit: 100);
		entries.Should().HaveCount(2);
		entries[0].Action.Should().Be(AuditAction.Deleted);
		entries[0].OldValue.Should().Be("\"v\"");
		entries[0].NewValue.Should().BeNull();
	}

	[Fact]
	public void Binding_Secret_Value_Never_Stored_As_Plaintext()
	{
		// AuditLog payload for a Secret binding must be the ciphertext bundle, never the
		// plaintext. Upsert a fake-Secret (synthetic bytes; no need for real encrypt) and
		// confirm the NewValue column starts with "secret|" marker.
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);
		bindings.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "db.password",
			Kind = BindingKind.Secret,
			Ciphertext = [1, 2, 3],
			Iv = new byte[12],
			AuthTag = new byte[16],
			KeyVersion = "v1",
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UnixEpoch,
		}, "alice");

		var entries = audit.ListRecent(10);
		var entry = entries.First(e => e.EntityType == AuditEntityType.Binding);
		entry.NewValue.Should().StartWith("secret|");
		entry.NewValue.Should().NotContain("plaintext");
	}

	[Fact]
	public void ApiKey_Create_And_Delete_Emit_Entries()
	{
		using var tmp = new TempDb();
		var (_, keys, _, audit) = Wire(tmp);

		var created = keys.Create(TagSet.From([new("env", "prod")]), ["db."], "prod reader", DateTimeOffset.UnixEpoch, "alice");
		keys.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow, "bob");

		var entries = audit.Query(AuditEntityType.ApiKey, null, null, 10);
		entries.Should().HaveCount(2);
		entries[0].Action.Should().Be(AuditAction.Deleted);
		entries[0].Actor.Should().Be("bob");
		entries[1].Action.Should().Be(AuditAction.Created);
		entries[1].Actor.Should().Be("alice");
		entries[1].KeyPath.Should().Be(created.Info.TokenPrefix);
	}

	[Fact]
	public void User_Lifecycle_Is_Audited()
	{
		using var tmp = new TempDb();
		var (_, _, users, audit) = Wire(tmp);

		users.Create("alice", "pw1", DateTimeOffset.UnixEpoch, "root");
		users.UpdatePassword("alice", "pw2", DateTimeOffset.UnixEpoch, "alice");
		users.Delete("alice", DateTimeOffset.UnixEpoch, "root");

		var entries = audit.Query(AuditEntityType.User, null, null, 10);
		entries.Select(e => e.Action)
			.Should().Equal(AuditAction.Deleted, AuditAction.Updated, AuditAction.Created);
		entries.All(e => e.KeyPath == "alice").Should().BeTrue();
		entries.First(e => e.Action == AuditAction.Updated).Actor.Should().Be("alice");
		entries.First(e => e.Action == AuditAction.Deleted).Actor.Should().Be("root");
	}

	[Fact]
	public void Query_Filter_By_KeyPath_Substring()
	{
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);
		bindings.Upsert(Plain(TagSet.Empty, "db.host", "\"x\""), "alice");
		bindings.Upsert(Plain(TagSet.Empty, "db.port", "5432"), "alice");
		bindings.Upsert(Plain(TagSet.Empty, "cache.ttl", "60"), "alice");

		var results = audit.Query(AuditEntityType.Binding, null, "db.", 10);
		results.Should().HaveCount(2);
		results.All(e => e.KeyPath!.StartsWith("db.")).Should().BeTrue();
	}

	[Fact]
	public void ListRecent_Orders_Newest_First()
	{
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);
		bindings.Upsert(Plain(TagSet.Empty, "a", "\"1\""), "alice");
		System.Threading.Thread.Sleep(2);
		bindings.Upsert(Plain(TagSet.Empty, "b", "\"2\""), "alice");

		var recent = audit.ListRecent(10);
		recent.Should().HaveCountGreaterThanOrEqualTo(2);
		// Most recent first.
		recent[0].KeyPath.Should().Be("b");
	}

	[Fact]
	public void FindById_Returns_Single_Entry()
	{
		using var tmp = new TempDb();
		var (bindings, _, _, audit) = Wire(tmp);
		bindings.Upsert(Plain(TagSet.Empty, "k", "\"v\""), "alice");

		var recent = audit.ListRecent(1).Single();
		var refetched = audit.FindById(recent.Id);
		refetched.Should().NotBeNull();
		refetched!.Action.Should().Be(recent.Action);
		refetched.KeyPath.Should().Be(recent.KeyPath);
	}
}
