using System.Text.Json;
using YobaConf.Core;
using YobaConf.Core.Security;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests;

public sealed class ResolvePipelineWithSecretsTests
{
	const string TestKeyBase64 = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=";

	static AesGcmSecretEncryptor MakeEncryptor() => new(TestKeyBase64);

	[Fact]
	public void Secret_In_Scope_AppearsDecrypted_InResolvedJson()
	{
		var enc = MakeEncryptor();
		var bundle = enc.Encrypt("prod-password");

		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "db = ${db_password}", DateTimeOffset.UnixEpoch);
		store.UpsertSecret(
			NodePath.ParseDb("app"),
			"db_password",
			bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion,
			DateTimeOffset.UnixEpoch);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store, enc);

		using var doc = JsonDocument.Parse(result.Json);
		doc.RootElement.GetProperty("db").GetString().Should().Be("prod-password");
	}

	[Fact]
	public void Secret_Inherited_FromAncestor_ReachesDescendant()
	{
		var enc = MakeEncryptor();
		var bundle = enc.Encrypt("ancestor-secret");

		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("project-a/prod"), "db = ${api_key}", DateTimeOffset.UnixEpoch);
		store.UpsertSecret(
			NodePath.ParseDb("project-a"),
			"api_key",
			bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion,
			DateTimeOffset.UnixEpoch);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("project-a/prod"), store, enc);

		using var doc = JsonDocument.Parse(result.Json);
		doc.RootElement.GetProperty("db").GetString().Should().Be("ancestor-secret");
	}

	[Fact]
	public void Secret_Wins_Over_Variable_AtSameScope()
	{
		// VariableScopeResolver: Secret wins at equal scope. Round-trip through the
		// pipeline confirms the override lands in the final HOCON fragment.
		var enc = MakeEncryptor();
		var bundle = enc.Encrypt("from-secret");

		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "value = ${key}", DateTimeOffset.UnixEpoch);
		store.UpsertVariable(NodePath.ParseDb("app"), "key", "from-variable", DateTimeOffset.UnixEpoch);
		store.UpsertSecret(
			NodePath.ParseDb("app"),
			"key",
			bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion,
			DateTimeOffset.UnixEpoch);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store, enc);

		using var doc = JsonDocument.Parse(result.Json);
		doc.RootElement.GetProperty("value").GetString().Should().Be("from-secret");
	}

	[Fact]
	public void Secrets_Present_But_EncryptorNull_Throws_WithGuidance()
	{
		// Fail-loud path: if ResolvePipeline is called without an encryptor but there
		// are secrets to decrypt, we throw rather than silently skip — silent skip
		// would leave ${secret_key} substitutions dangling and produce a misleading
		// HoconParserException downstream.
		var enc = MakeEncryptor();
		var bundle = enc.Encrypt("x");

		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "x = 1", DateTimeOffset.UnixEpoch);
		store.UpsertSecret(
			NodePath.ParseDb("app"),
			"api_key",
			bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion,
			DateTimeOffset.UnixEpoch);

		var act = () => ResolvePipeline.Resolve(NodePath.ParseDb("app"), store, encryptor: null);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*secret(s) in scope*YOBACONF_MASTER_KEY*");
	}

	[Fact]
	public void NoSecrets_NullEncryptor_Works()
	{
		// Phase-A compatibility: existing callers (tests, Node page without secrets
		// registered) pass null encryptor and Resolve works as before.
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "x = 42", DateTimeOffset.UnixEpoch);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store, encryptor: null);

		using var doc = JsonDocument.Parse(result.Json);
		doc.RootElement.GetProperty("x").GetInt32().Should().Be(42);
	}

	[Fact]
	public void ETag_Changes_When_Secret_Changes()
	{
		// If the decrypted plaintext changes, the resolved JSON changes, so ETag must
		// change. This is the invariant behind cache invalidation when a secret is rotated.
		var enc = MakeEncryptor();
		var bundleA = enc.Encrypt("value-a");
		var bundleB = enc.Encrypt("value-b");

		var storeA = new InMemoryConfigStore();
		storeA.UpsertNode(NodePath.ParseDb("app"), "v = ${k}", DateTimeOffset.UnixEpoch);
		storeA.UpsertSecret(NodePath.ParseDb("app"), "k",
			bundleA.Ciphertext, bundleA.Iv, bundleA.AuthTag, bundleA.KeyVersion, DateTimeOffset.UnixEpoch);

		var storeB = new InMemoryConfigStore();
		storeB.UpsertNode(NodePath.ParseDb("app"), "v = ${k}", DateTimeOffset.UnixEpoch);
		storeB.UpsertSecret(NodePath.ParseDb("app"), "k",
			bundleB.Ciphertext, bundleB.Iv, bundleB.AuthTag, bundleB.KeyVersion, DateTimeOffset.UnixEpoch);

		var etagA = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeA, enc).ETag;
		var etagB = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeB, enc).ETag;

		etagA.Should().NotBe(etagB);
	}
}
