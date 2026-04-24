using Microsoft.Extensions.Options;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Storage;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Auth;

public sealed class SqliteApiKeyStoreTests
{
	static SqliteApiKeyStore CreateStore(TempDb tmp) =>
		new(Options.Create(new SqliteBindingStoreOptions
		{
			DataDirectory = tmp.Directory,
			FileName = tmp.FileName,
		}));

	[Fact]
	public void Create_ReturnsPlaintext_Once_AndHashesAreStored()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var tags = TagSet.From([new("env", "prod")]);

		var created = store.Create(tags, null, "prod ops key", DateTimeOffset.UnixEpoch);

		created.Plaintext.Should().HaveLength(22);
		created.Info.TokenPrefix.Should().Be(created.Plaintext[..6]);
		created.Info.RequiredTags.CanonicalJson.Should().Be(tags.CanonicalJson);

		// Stored list doesn't carry plaintext.
		var listed = store.ListActive().Single();
		listed.Id.Should().Be(created.Info.Id);
		listed.Description.Should().Be("prod ops key");
	}

	[Fact]
	public void Validate_Success_On_KnownToken()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(TagSet.From([new("env", "prod")]), null, "k", DateTimeOffset.UnixEpoch);

		var outcome = store.Validate(created.Plaintext);

		var valid = outcome.Should().BeOfType<ApiKeyValidation.Valid>().Subject;
		valid.Key.Id.Should().Be(created.Info.Id);
		valid.Key.TokenPrefix.Should().Be(created.Info.TokenPrefix);
	}

	[Fact]
	public void Validate_Fails_On_Null_Empty_OrWrongToken()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		store.Create(TagSet.Empty, null, "k", DateTimeOffset.UnixEpoch);

		store.Validate(null).Should().BeOfType<ApiKeyValidation.Invalid>();
		store.Validate(string.Empty).Should().BeOfType<ApiKeyValidation.Invalid>();
		store.Validate("NOTaV4lIDT0kenXXXXXX22").Should().BeOfType<ApiKeyValidation.Invalid>();
	}

	[Fact]
	public void Validate_Fails_On_SoftDeleted_Key()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(TagSet.Empty, null, "k", DateTimeOffset.UnixEpoch);

		store.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow).Should().BeTrue();
		store.Validate(created.Plaintext).Should().BeOfType<ApiKeyValidation.Invalid>();
	}

	[Fact]
	public void CheckScope_Succeeds_When_TagVector_Contains_RequiredTags()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(
			TagSet.From([new("env", "prod"), new("project", "yobapub")]),
			null, "k", DateTimeOffset.UnixEpoch);

		var valid = (ApiKeyValidation.Valid)store.Validate(created.Plaintext);
		IApiKeyStore.CheckScope(valid.Key, new Dictionary<string, string>
		{
			["env"] = "prod",
			["project"] = "yobapub",
			["region"] = "eu-west",
		}).Should().BeNull();
	}

	[Fact]
	public void CheckScope_Fails_When_TagVector_Missing_Required()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(
			TagSet.From([new("env", "prod"), new("project", "yobapub")]),
			null, "k", DateTimeOffset.UnixEpoch);

		var valid = (ApiKeyValidation.Valid)store.Validate(created.Plaintext);
		IApiKeyStore.CheckScope(valid.Key, new Dictionary<string, string>
		{
			["env"] = "prod",
		}).Should().NotBeNull();
	}

	[Fact]
	public void CheckScope_Fails_When_Values_Differ()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(TagSet.From([new("env", "prod")]), null, "k", DateTimeOffset.UnixEpoch);

		var valid = (ApiKeyValidation.Valid)store.Validate(created.Plaintext);
		IApiKeyStore.CheckScope(valid.Key, new Dictionary<string, string>
		{
			["env"] = "staging",
		}).Should().NotBeNull();
	}

	[Fact]
	public void AllowedKeyPrefixes_Roundtrip()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		var created = store.Create(
			TagSet.Empty,
			["db.", "cache."],
			"scoped reader",
			DateTimeOffset.UnixEpoch);

		var valid = (ApiKeyValidation.Valid)store.Validate(created.Plaintext);
		valid.Key.AllowedKeyPrefixes.Should().BeEquivalentTo(["db.", "cache."]);
	}

	[Fact]
	public void ListActive_Excludes_SoftDeleted()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);

		var first = store.Create(TagSet.Empty, null, "a", DateTimeOffset.UnixEpoch);
		var second = store.Create(TagSet.Empty, null, "b", DateTimeOffset.UnixEpoch);
		store.SoftDelete(first.Info.Id, DateTimeOffset.UtcNow);

		store.ListActive().Should().ContainSingle(i => i.Id == second.Info.Id);
	}

	[Fact]
	public void SoftDelete_Returns_False_On_Missing_Or_Repeated()
	{
		using var tmp = new TempDb();
		var store = CreateStore(tmp);
		store.SoftDelete(9999, DateTimeOffset.UtcNow).Should().BeFalse();

		var created = store.Create(TagSet.Empty, null, "k", DateTimeOffset.UnixEpoch);
		store.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow).Should().BeTrue();
		store.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow).Should().BeFalse();
	}
}
