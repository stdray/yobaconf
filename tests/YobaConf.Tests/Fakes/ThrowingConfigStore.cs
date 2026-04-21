namespace YobaConf.Tests.Fakes;

// IConfigStore double that throws from every method — used to simulate "DB is down / not
// yet migrated" for the /ready probe test. Every call surfaces a distinctive exception
// message so failed tests make it obvious which endpoint trips.
public sealed class ThrowingConfigStore : IConfigStore
{
	readonly string message;

	public ThrowingConfigStore(string message = "store intentionally unavailable") =>
		this.message = message;

	public HoconNode? FindNode(NodePath path) => throw new InvalidOperationException(message);

	public IReadOnlyList<NodePath> ListNodePaths() => throw new InvalidOperationException(message);

	public IReadOnlyList<Variable> FindVariables(NodePath scope) => throw new InvalidOperationException(message);

	public IReadOnlyList<Secret> FindSecrets(NodePath scope) => throw new InvalidOperationException(message);
}
