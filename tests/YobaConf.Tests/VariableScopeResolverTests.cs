using YobaConf.Tests.Fakes;

namespace YobaConf.Tests;

public class VariableScopeResolverTests
{
	// Helper factories — tests don't care about timestamp/AES-payload bytes; just need
	// unique-ish placeholders. Keeps the test-body noise down.
	static Variable Var(string scope, string key, string value) =>
		new(key, value, NodePath.ParseDb(scope), DateTimeOffset.UnixEpoch);

	static Secret Sec(string scope, string key) =>
		new(key, [0x01], [0x02], [0x03], "v1", NodePath.ParseDb(scope), DateTimeOffset.UnixEpoch);

	[Fact]
	public void Variable_AtRequestedScope_IsReturned()
	{
		var store = new InMemoryConfigStore(variables: [Var("app", "db_host", "localhost")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("app"), store);

		set.Variables.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(Var("app", "db_host", "localhost"));
		set.Secrets.Should().BeEmpty();
	}

	[Fact]
	public void Variable_AtAncestorScope_IsInheritedByDescendant()
	{
		// Variable at `project-a` should be visible at `project-a/test/service1`.
		var store = new InMemoryConfigStore(variables: [Var("project-a", "db_host", "parent-host")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		set.Variables.Should().ContainSingle()
			.Which.Value.Should().Be("parent-host");
	}

	[Fact]
	public void Variable_AtSiblingScope_IsNotVisible()
	{
		// Variable at `project-a/dev` is not visible from `project-a/test` — sibling, not ancestor.
		var store = new InMemoryConfigStore(variables: [Var("project-a/dev", "db_host", "dev-host")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().BeEmpty();
	}

	[Fact]
	public void NearerScope_Wins_OverFartherScope_SameKey()
	{
		// Same Key `db_host` at both `project-a` and `project-a/test`. Request at test-level
		// must see the nearer (test-level) value.
		var store = new InMemoryConfigStore(variables:
		[
			Var("project-a", "db_host", "parent-host"),
			Var("project-a/test", "db_host", "test-host"),
		]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().ContainSingle()
			.Which.Value.Should().Be("test-host");
	}

	[Fact]
	public void Secret_AtEqualScope_WinsOverVariable_WithSameKey()
	{
		// Both a Variable and a Secret named `api_key` at the same scope. Secret wins —
		// explicit secret declaration beats a same-name plaintext variable.
		var store = new InMemoryConfigStore(
			variables: [Var("project-a", "api_key", "plaintext-value")],
			secrets: [Sec("project-a", "api_key")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a"), store);

		set.Variables.Should().BeEmpty();
		set.Secrets.Should().ContainSingle()
			.Which.Key.Should().Be("api_key");
	}

	[Fact]
	public void Secret_AtNearerScope_WinsOverVariable_AtFartherScope()
	{
		var store = new InMemoryConfigStore(
			variables: [Var("project-a", "api_key", "parent-plaintext")],
			secrets: [Sec("project-a/test", "api_key")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().BeEmpty();
		set.Secrets.Should().ContainSingle();
	}

	[Fact]
	public void Variable_AtNearerScope_WinsOverSecret_AtFartherScope()
	{
		// Inverse of previous: Variable at nearer scope shadows a Secret at farther scope.
		// Nearest-scope wins regardless of type.
		var store = new InMemoryConfigStore(
			variables: [Var("project-a/test", "api_key", "test-plaintext")],
			secrets: [Sec("project-a", "api_key")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().ContainSingle()
			.Which.Value.Should().Be("test-plaintext");
		set.Secrets.Should().BeEmpty();
	}

	[Fact]
	public void RootScope_IsVisibleToAllDescendants()
	{
		// A variable at `""` (root) is visible to every descendant path.
		var store = new InMemoryConfigStore(variables: [Var("", "log_level", "info")]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		set.Variables.Should().ContainSingle()
			.Which.ScopePath.Should().Be(NodePath.Root);
	}

	[Fact]
	public void SoftDeleted_Variable_IsSkipped()
	{
		var store = new InMemoryConfigStore(variables:
		[
			new Variable("db_host", "tombstone", NodePath.ParseDb("app"), DateTimeOffset.UnixEpoch, IsDeleted: true),
		]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("app"), store);

		set.Variables.Should().BeEmpty();
	}

	[Fact]
	public void SoftDeleted_Secret_IsSkipped()
	{
		var store = new InMemoryConfigStore(secrets:
		[
			new Secret("api_key", [1], [2], [3], "v1", NodePath.ParseDb("app"), DateTimeOffset.UnixEpoch, IsDeleted: true),
		]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("app"), store);

		set.Secrets.Should().BeEmpty();
	}

	[Fact]
	public void SoftDeleted_AtNearerScope_Exposes_UnderlyingLiveEntry_AtFartherScope()
	{
		// Soft-delete at nearer scope doesn't "hide" the farther-scope entry — dedup is
		// by *live* rows only. Tombstone → farther scope becomes visible again.
		var store = new InMemoryConfigStore(variables:
		[
			Var("project-a", "db_host", "parent-host"),
			new Variable("db_host", "tombstone", NodePath.ParseDb("project-a/test"), DateTimeOffset.UnixEpoch, IsDeleted: true),
		]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().ContainSingle()
			.Which.Value.Should().Be("parent-host");
	}

	[Fact]
	public void NoVariablesOrSecrets_ReturnsEmptySet()
	{
		var store = new InMemoryConfigStore();

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("app"), store);

		// `set.Should().Be(VariableSet.Empty)` would rely on record equality, but
		// ImmutableArray<T> has default struct equality (not sequence-based), so two
		// "empty" VariableSets compare as !=. Check contents directly.
		set.Variables.Should().BeEmpty();
		set.Secrets.Should().BeEmpty();
	}

	[Fact]
	public void Request_At_Root_Collects_RootScope_Only()
	{
		// Root-level request sees its own scope. Nothing to inherit (Root has no parent).
		var store = new InMemoryConfigStore(variables:
		[
			Var("", "root_var", "root-value"),
			Var("project-a", "nested_var", "nested-value"),
		]);

		var set = VariableScopeResolver.Resolve(NodePath.Root, store);

		set.Variables.Should().ContainSingle()
			.Which.Key.Should().Be("root_var");
	}

	[Fact]
	public void MultipleKeys_AtMultipleScopes_AllVisible_WithCorrectWinner()
	{
		// Mixed scenario: several keys, some shadowed, some inherited, some at nearer scope only.
		var store = new InMemoryConfigStore(variables:
		[
			Var("", "log_level", "info"),             // root — inherits down
			Var("project-a", "db_host", "parent-db"), // mid — inherits to test
			Var("project-a", "port", "5432"),         // mid — will be shadowed
			Var("project-a/test", "port", "6000"),    // nearer — shadows parent's port
			Var("project-a/test", "feature", "on"),   // leaf-only
		]);

		var set = VariableScopeResolver.Resolve(NodePath.ParseDb("project-a/test"), store);

		set.Variables.Should().HaveCount(4);
		set.Variables.Should().Contain(v => v.Key == "log_level" && v.Value == "info");
		set.Variables.Should().Contain(v => v.Key == "db_host" && v.Value == "parent-db");
		set.Variables.Should().Contain(v => v.Key == "port" && v.Value == "6000");
		set.Variables.Should().Contain(v => v.Key == "feature" && v.Value == "on");
	}
}
