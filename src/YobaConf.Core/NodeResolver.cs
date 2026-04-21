using System.Collections.Immutable;

namespace YobaConf.Core;

// Fallthrough + ancestor-chain collection, spec §4.3.
public static class NodeResolver
{
	// Walk from `path` up to root; return the first existing node.
	public static HoconNode? FindBestMatch(IConfigStore store, NodePath path)
	{
		for (NodePath? current = path; current is not null; current = current.Value.Parent)
		{
			var node = store.FindNode(current.Value);
			if (node is not null)
				return node;
		}
		return null;
	}

	// Collect all existing ancestor nodes (inclusive of `path` if present), ordered root → leaf.
	// Used to feed the HOCON `.WithFallback()` chain in §4.4.
	public static ImmutableArray<HoconNode> CollectAncestorChain(IConfigStore store, NodePath path)
	{
		var stack = new Stack<HoconNode>();
		for (NodePath? current = path; current is not null; current = current.Value.Parent)
		{
			var node = store.FindNode(current.Value);
			if (node is not null)
				stack.Push(node);
		}
		return [.. stack];
	}
}
