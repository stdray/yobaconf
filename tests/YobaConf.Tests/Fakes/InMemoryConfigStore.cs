namespace YobaConf.Tests.Fakes;

public sealed class InMemoryConfigStore(IReadOnlyDictionary<NodePath, HoconNode> nodes) : IConfigStore
{
	public HoconNode? FindNode(NodePath path) => nodes.TryGetValue(path, out var node) ? node : null;

	public static InMemoryConfigStore With(params (string dbPath, string hocon)[] entries) =>
		new(entries.ToDictionary(
			e => NodePath.ParseDb(e.dbPath),
			e => new HoconNode(NodePath.ParseDb(e.dbPath), e.hocon, DateTimeOffset.UnixEpoch)));
}
