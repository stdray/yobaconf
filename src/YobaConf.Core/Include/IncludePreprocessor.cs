using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace YobaConf.Core.Include;

// Expands `include "absolute-path"` directives in HOCON text via DFS + cycle detection + scope
// validation. Produces a flat HOCON string with no include directives remaining, which is then
// handed to Hocon.HoconConfigurationFactory.ParseString (spec §4.4).
//
// Why a preprocess step instead of HOCON's native ConfigResolver callback: the callback
// doesn't get "who is doing the including" context, so scope validation (dir(target) must be
// ancestor-or-equal of dir(including)) and proper cycle detection (per-chain, not global) can't
// be expressed on its API cleanly. See decision-log.md 2026-04-21 "Include-семантика
// финализирована".
public static partial class IncludePreprocessor
{
	public static string Resolve(NodePath rootPath, IConfigStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		var inflight = new HashSet<NodePath>();
		var stack = new Stack<NodePath>();
		return Expand(rootPath, inflight, stack, store);
	}

	static string Expand(NodePath node, HashSet<NodePath> inflight, Stack<NodePath> stack, IConfigStore store)
	{
		if (!inflight.Add(node))
		{
			// Reconstruct the chain including the re-entered node for a readable message.
			var chain = stack.Reverse().Append(node).ToImmutableArray();
			throw new CyclicIncludeException(chain);
		}
		stack.Push(node);
		try
		{
			var row = store.FindNode(node)
				?? throw new IncludeTargetNotFoundException(node);

			return IncludeLineRegex().Replace(row.RawContent, match =>
			{
				var target = ParseIncludeBody(match.Groups["body"].Value);
				ValidateScope(node, target);
				return Expand(target, inflight, stack, store);
			});
		}
		finally
		{
			stack.Pop();
			inflight.Remove(node);
		}
	}

	// Matches a line that starts with optional whitespace, the keyword `include`, then the rest
	// of the line as `body`. Lookahead stops before the line terminator so we don't consume it.
	// Multi-line content inside triple-quoted strings that literally contains `include` at line
	// start is an MVP limitation — document and revisit if it bites.
	[GeneratedRegex(@"^[ \t]*include[ \t]+(?<body>[^\r\n]+?)[ \t]*(?=\r?\n|\z)", RegexOptions.Multiline)]
	private static partial Regex IncludeLineRegex();

	// Acceptable form: "absolute-path" optionally followed by a HOCON comment (`#` or `//`).
	[GeneratedRegex(@"^""(?<path>(?:[^""\\]|\\.)*)""\s*(?:#.*|//.*)?$")]
	private static partial Regex QuotedPathRegex();

	// HOCON variants we don't support in MVP; each hijacks parser semantics we don't want to
	// replicate without a concrete use case (file/classpath/url go outside YobaConf; `required`
	// has its own fail-semantic orthogonal to our missing-target behaviour).
	[GeneratedRegex(@"^(?:file|classpath|url|required)\s*\(")]
	private static partial Regex UnsupportedFormRegex();

	static NodePath ParseIncludeBody(string body)
	{
		var trimmed = body.Trim();

		if (UnsupportedFormRegex().IsMatch(trimmed))
			throw new UnsupportedIncludeSyntaxException(trimmed, "only `include \"absolute-path\"` is supported in MVP");

		var match = QuotedPathRegex().Match(trimmed);
		if (!match.Success)
			throw new UnsupportedIncludeSyntaxException(trimmed, "expected a quoted absolute path");

		var path = match.Groups["path"].Value;

		if (path.Contains("..", StringComparison.Ordinal))
			throw new UnsupportedIncludeSyntaxException(trimmed, "relative include paths are not supported in MVP (use absolute from root)");

		try
		{
			return NodePath.ParseDb(path);
		}
		catch (ArgumentException ex)
		{
			throw new UnsupportedIncludeSyntaxException(trimmed, ex.Message);
		}
	}

	// Per spec §1 and decision-log 2026-04-21 "Include-семантика финализирована":
	//   - target's parent dir must be ancestor-or-equal of including node's parent dir
	//   - self-include is forbidden
	//   => permits ancestors of the including node AND siblings within the same directory;
	//      rejects descendants, sibling-subtrees, cross-tree references.
	static void ValidateScope(NodePath including, NodePath target)
	{
		if (target.Equals(including))
			throw new IncludeScopeViolationException(including, target, "self-include");

		var includingDir = including.Parent ?? NodePath.Root;
		var targetDir = target.Parent ?? NodePath.Root;

		if (targetDir.Equals(includingDir) || targetDir.IsAncestorOf(includingDir))
			return;

		throw new IncludeScopeViolationException(including, target,
			$"dir('{targetDir.ToDbPath()}') is not ancestor-or-equal of dir('{includingDir.ToDbPath()}')");
	}
}
