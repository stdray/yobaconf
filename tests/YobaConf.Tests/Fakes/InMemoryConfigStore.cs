namespace YobaConf.Tests.Fakes;

// In-memory IConfigStore + IConfigStoreAdmin double for unit and integration tests.
// Mutable collections so admin-UI tests can seed + mutate from the test body.
public sealed class InMemoryConfigStore : IConfigStore, IConfigStoreAdmin
{
	readonly Dictionary<NodePath, HoconNode> nodes;
	readonly Dictionary<NodePath, List<Variable>> variables;
	readonly Dictionary<NodePath, List<Secret>> secrets;

	public InMemoryConfigStore(
		IReadOnlyDictionary<NodePath, HoconNode>? nodes = null,
		IEnumerable<Variable>? variables = null,
		IEnumerable<Secret>? secrets = null)
	{
		this.nodes = nodes is null
			? []
			: new Dictionary<NodePath, HoconNode>(nodes);
		this.variables = (variables ?? []).GroupBy(v => v.ScopePath)
			.ToDictionary(g => g.Key, g => g.ToList());
		this.secrets = (secrets ?? []).GroupBy(s => s.ScopePath)
			.ToDictionary(g => g.Key, g => g.ToList());
	}

	public HoconNode? FindNode(NodePath path) =>
		nodes.TryGetValue(path, out var node) ? node : null;

	public IReadOnlyList<NodePath> ListNodePaths() =>
		[.. nodes.Keys.OrderBy(p => p.ToDbPath(), StringComparer.Ordinal)];

	public IReadOnlyList<Variable> FindVariables(NodePath scope) =>
		variables.TryGetValue(scope, out var list) ? [.. list] : [];

	public IReadOnlyList<Secret> FindSecrets(NodePath scope) =>
		secrets.TryGetValue(scope, out var list) ? [.. list] : [];

	public void UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt) =>
		nodes[path] = new HoconNode(path, rawContent, updatedAt);

	public void UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt)
	{
		var list = variables.TryGetValue(scope, out var existing) ? existing : (variables[scope] = []);
		list.RemoveAll(v => v.Key == key);
		list.Add(new Variable(key, value, scope, updatedAt));
	}

	public void UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt)
	{
		var list = secrets.TryGetValue(scope, out var existing) ? existing : (secrets[scope] = []);
		list.RemoveAll(s => s.Key == key);
		list.Add(new Secret(key, encryptedValue, iv, authTag, keyVersion, scope, updatedAt));
	}

	public void SoftDeleteNode(NodePath path) => nodes.Remove(path);

	public void SoftDeleteVariable(NodePath scope, string key)
	{
		if (variables.TryGetValue(scope, out var list))
			list.RemoveAll(v => v.Key == key);
	}

	public void SoftDeleteSecret(NodePath scope, string key)
	{
		if (secrets.TryGetValue(scope, out var list))
			list.RemoveAll(s => s.Key == key);
	}

	// Tuple-shorthand for existing node-only tests (FallthroughTests, IncludePreprocessorTests)
	// — those don't touch variables/secrets.
	public static InMemoryConfigStore With(params (string dbPath, string hocon)[] entries)
	{
		var nodes = entries.ToDictionary(
			e => NodePath.ParseDb(e.dbPath),
			e => new HoconNode(NodePath.ParseDb(e.dbPath), e.hocon, DateTimeOffset.UnixEpoch));
		return new InMemoryConfigStore(nodes);
	}
}
