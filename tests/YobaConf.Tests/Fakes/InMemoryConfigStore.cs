using System.Collections.Immutable;

namespace YobaConf.Tests.Fakes;

public sealed class InMemoryConfigStore : IConfigStore
{
	readonly IReadOnlyDictionary<NodePath, HoconNode> nodes;
	readonly ILookup<NodePath, Variable> variables;
	readonly ILookup<NodePath, Secret> secrets;

	public InMemoryConfigStore(
		IReadOnlyDictionary<NodePath, HoconNode>? nodes = null,
		IEnumerable<Variable>? variables = null,
		IEnumerable<Secret>? secrets = null)
	{
		this.nodes = nodes ?? ImmutableDictionary<NodePath, HoconNode>.Empty;
		this.variables = (variables ?? []).ToLookup(v => v.ScopePath);
		this.secrets = (secrets ?? []).ToLookup(s => s.ScopePath);
	}

	public HoconNode? FindNode(NodePath path) => nodes.TryGetValue(path, out var node) ? node : null;

	public IReadOnlyList<Variable> FindVariables(NodePath scope) => [.. variables[scope]];

	public IReadOnlyList<Secret> FindSecrets(NodePath scope) => [.. secrets[scope]];

	// Tuple-shorthand for existing node-only tests (FallthroughTests, IncludePreprocessorTests).
	// New tests needing variables/secrets call the full constructor directly.
	public static InMemoryConfigStore With(params (string dbPath, string hocon)[] entries)
	{
		var nodes = entries.ToDictionary(
			e => NodePath.ParseDb(e.dbPath),
			e => new HoconNode(NodePath.ParseDb(e.dbPath), e.hocon, DateTimeOffset.UnixEpoch));
		return new InMemoryConfigStore(nodes);
	}
}
