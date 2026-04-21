using System.Collections.Immutable;

namespace YobaConf.Core.Include;

public abstract class IncludeException(string message) : Exception(message);

// `include A -> include B -> include A` or any longer cycle back to a node already on the
// current resolution stack. Carries the full chain so users can see which node closes the loop.
public sealed class CyclicIncludeException : IncludeException
{
	public ImmutableArray<NodePath> Chain { get; }

	public CyclicIncludeException(ImmutableArray<NodePath> chain)
		: base($"Cyclic include detected: {string.Join(" -> ", chain.Select(p => p.ToDbPath()))}")
	{
		Chain = chain;
	}
}

// The include target exists in the text but violates the scope rule (spec §1 / decision-log
// 2026-04-21 "Include-семантика финализирована"): dir(target) must be ancestor-or-equal of
// dir(including). Self-include is also a violation.
public sealed class IncludeScopeViolationException : IncludeException
{
	public NodePath IncludingNode { get; }
	public NodePath Target { get; }

	public IncludeScopeViolationException(NodePath includingNode, NodePath target, string reason)
		: base($"Include '{target.ToDbPath()}' from '{includingNode.ToDbPath()}' violates scope rule: {reason}")
	{
		IncludingNode = includingNode;
		Target = target;
	}
}

// `include "x"` where x has no row in IConfigStore (or is soft-deleted once IsDeleted lands).
public sealed class IncludeTargetNotFoundException : IncludeException
{
	public NodePath Target { get; }

	public IncludeTargetNotFoundException(NodePath target)
		: base($"Include target '{target.ToDbPath()}' does not exist.")
	{
		Target = target;
	}
}

// HOCON allows more include variants than we support in MVP (`include file(...)`,
// `include classpath(...)`, `include url(...)`, `include required(...)`, relative paths with
// `..`). Anything non-simple throws this with the offending body for diagnostics.
public sealed class UnsupportedIncludeSyntaxException(string body, string reason)
	: IncludeException($"Unsupported include syntax '{body}': {reason}")
{
	public string Body { get; } = body;
}
