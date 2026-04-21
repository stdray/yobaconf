namespace YobaConf.Core;

// Thrown by ResolvePipeline when Fallthrough walks all the way to Root and finds nothing
// existing. Mapped to HTTP 404 at the API surface (spec §4.3).
public sealed class NodeNotFoundException(NodePath requestedPath)
	: Exception($"No node exists at '{requestedPath.ToDbPath()}' or any of its ancestors.")
{
	public NodePath RequestedPath { get; } = requestedPath;
}
