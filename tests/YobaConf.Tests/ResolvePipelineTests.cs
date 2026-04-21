using System.Text.Json.Nodes;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests;

public class ResolvePipelineTests
{
	static Variable Var(string scope, string key, string value) =>
		new(key, value, NodePath.ParseDb(scope), DateTimeOffset.UnixEpoch);

	[Fact]
	public void Simple_Node_Produces_CanonicalJson_And_Etag()
	{
		var store = InMemoryConfigStore.With(("app", "name = yoba\nport = 8080"));

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		result.Json.Should().Be("""{"name":"yoba","port":8080}""");
		result.ETag.Should().HaveLength(16)
			.And.MatchRegex("^[0-9a-f]{16}$");
	}

	[Fact]
	public void Missing_Node_AndNo_Ancestor_Throws_NodeNotFound()
	{
		var store = InMemoryConfigStore.With(("other", "x = 1"));

		var act = () => ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<NodeNotFoundException>()
			.Which.RequestedPath.Should().Be(NodePath.ParseDb("app"));
	}

	[Fact]
	public void Fallthrough_Serves_NearestAncestor_When_ExactPath_Missing()
	{
		// Request `/app/dev/feature`; fallthrough finds `/app/dev` and returns its content.
		var store = InMemoryConfigStore.With(
			("app", "a = root"),
			("app/dev", "a = dev-specific\nb = extra"));

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app/dev/feature"), store);

		result.Json.Should().Be("""{"a":"dev-specific","b":"extra"}""");
	}

	[Fact]
	public void Variables_Substitute_Into_NodeContent()
	{
		// `${db_host}` reference in node content resolves to the variable's value.
		var store = new InMemoryConfigStore(
			nodes: new Dictionary<NodePath, HoconNode>
			{
				[NodePath.ParseDb("app")] = new(NodePath.ParseDb("app"), "connection = ${db_host}", DateTimeOffset.UnixEpoch),
			},
			variables: [Var("app", "db_host", "prod-db")]);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		// Variables render into the fragment as strings, so ${db_host} resolves to "prod-db".
		// `db_host` key itself also makes it into the final JSON as a leaked variable — that's
		// expected Phase A behaviour (variables are prepended as real HOCON, they become
		// visible members too). Users who want hidden vars can use optional `${?var}` pattern
		// or organise their configs to not leak.
		var parsed = JsonNode.Parse(result.Json)!;
		parsed["connection"]!.GetValue<string>().Should().Be("prod-db");
		parsed["db_host"]!.GetValue<string>().Should().Be("prod-db");
	}

	[Fact]
	public void Variables_Inherit_DownTree()
	{
		// Variable at root-scope is visible to a descendant node's substitution.
		var store = new InMemoryConfigStore(
			nodes: new Dictionary<NodePath, HoconNode>
			{
				[NodePath.ParseDb("app/prod")] = new(NodePath.ParseDb("app/prod"), "log = ${log_level}", DateTimeOffset.UnixEpoch),
			},
			variables: [Var("", "log_level", "info")]);

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app/prod"), store);

		JsonNode.Parse(result.Json)!["log"]!.GetValue<string>().Should().Be("info");
	}

	[Fact]
	public void Include_Directive_Pulls_AncestorContent()
	{
		var store = InMemoryConfigStore.With(
			("logger-base", "log_level = warn"),
			("app", "include \"logger-base\"\nname = yoba"));

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		result.Json.Should().Be("""{"log_level":"warn","name":"yoba"}""");
	}

	[Fact]
	public void Etag_Is_Deterministic_Across_Equivalent_Inputs()
	{
		// Two stores with the same logical config (different key-insertion order) must yield
		// the same ETag — the load-bearing determinism invariant for spec §4.6.
		var storeA = InMemoryConfigStore.With(("app", "a = 1\nb = 2\nc = 3"));
		var storeB = InMemoryConfigStore.With(("app", "c = 3\na = 1\nb = 2"));

		var etagA = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeA).ETag;
		var etagB = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeB).ETag;

		etagA.Should().Be(etagB);
	}

	[Fact]
	public void Etag_Changes_When_Content_Changes()
	{
		var storeA = InMemoryConfigStore.With(("app", "a = 1"));
		var storeB = InMemoryConfigStore.With(("app", "a = 2"));

		var etagA = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeA).ETag;
		var etagB = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeB).ETag;

		etagA.Should().NotBe(etagB);
	}

	[Fact]
	public void Etag_Changes_When_InScopeVariable_Changes()
	{
		// Even if the node content is identical, a variable that's referenced in it (or
		// rendered as a prefix key) affects the final JSON → affects ETag.
		var content = "conn = ${db_host}";
		var baseNode = new KeyValuePair<NodePath, HoconNode>(
			NodePath.ParseDb("app"),
			new HoconNode(NodePath.ParseDb("app"), content, DateTimeOffset.UnixEpoch));

		var storeA = new InMemoryConfigStore(
			nodes: new Dictionary<NodePath, HoconNode> { [baseNode.Key] = baseNode.Value },
			variables: [Var("app", "db_host", "host-a")]);
		var storeB = new InMemoryConfigStore(
			nodes: new Dictionary<NodePath, HoconNode> { [baseNode.Key] = baseNode.Value },
			variables: [Var("app", "db_host", "host-b")]);

		var etagA = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeA).ETag;
		var etagB = ResolvePipeline.Resolve(NodePath.ParseDb("app"), storeB).ETag;

		etagA.Should().NotBe(etagB);
	}

	[Fact]
	public void Full_Mix_Fallthrough_Variables_Include_All_Compose()
	{
		// End-to-end snapshot: request a missing leaf; fallthrough finds parent; variables
		// from root and mid scope both in play; an include pulls extra content.
		var store = new InMemoryConfigStore(
			nodes: new Dictionary<NodePath, HoconNode>
			{
				[NodePath.ParseDb("shared-logger")] = new(
					NodePath.ParseDb("shared-logger"), "log_format = json", DateTimeOffset.UnixEpoch),
				[NodePath.ParseDb("project-a/prod")] = new(
					NodePath.ParseDb("project-a/prod"),
					"include \"shared-logger\"\ndb = ${db_host}\nenv = prod",
					DateTimeOffset.UnixEpoch),
			},
			variables:
			[
				Var("", "log_level", "info"),
				Var("project-a", "db_host", "prod-db"),
			]);

		// Request a leaf that doesn't exist — fallthrough to `project-a/prod`.
		var result = ResolvePipeline.Resolve(NodePath.ParseDb("project-a/prod/feature-x"), store);

		var json = JsonNode.Parse(result.Json)!;
		json["db"]!.GetValue<string>().Should().Be("prod-db");
		json["env"]!.GetValue<string>().Should().Be("prod");
		json["log_format"]!.GetValue<string>().Should().Be("json");
		json["log_level"]!.GetValue<string>().Should().Be("info");
		json["db_host"]!.GetValue<string>().Should().Be("prod-db");
	}

	[Fact]
	public void Node_With_Empty_Object_Literal_Resolves_To_EmptyJsonObject()
	{
		var store = InMemoryConfigStore.With(("app", "{}"));

		var result = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		result.Json.Should().Be("{}");
		result.ETag.Should().HaveLength(16);
	}

	[Fact]
	public void Unresolved_RequiredSubstitution_Propagates_HoconParserException()
	{
		// Node references ${db_host} but no variable in scope defines it. At parse-time
		// Hocon throws — pipeline propagates without wrapping.
		var store = InMemoryConfigStore.With(("app", "conn = ${db_host}"));

		var act = () => ResolvePipeline.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<Hocon.HoconParserException>()
			.WithMessage("*Unresolved substitution*db_host*");
	}
}
