using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace YobaConf.Core;

public readonly partial record struct NodePath
{
	// Root == default(NodePath) by construction: both have null canonical and IsRoot==true.
#pragma warning disable CA1805 // intentional: documents Root as a named alias for default(NodePath)
	public static readonly NodePath Root = default;
#pragma warning restore CA1805

	readonly string? canonical;

	NodePath(string? canonical) => this.canonical = canonical;

	public bool IsRoot => string.IsNullOrEmpty(canonical);

	public ImmutableArray<string> Segments =>
		string.IsNullOrEmpty(canonical)
			? ImmutableArray<string>.Empty
			: [.. canonical.Split('/')];

	public NodePath? Parent
	{
		get
		{
			if (string.IsNullOrEmpty(canonical))
				return null;
			var lastSlash = canonical.LastIndexOf('/');
			return lastSlash < 0 ? Root : new NodePath(canonical[..lastSlash]);
		}
	}

	public string ToDbPath() => canonical ?? string.Empty;

	public string ToUrlPath() => (canonical ?? string.Empty).Replace('/', '.');

	public override string ToString() => ToDbPath();

	// True if `this` is a strict ancestor of `other` (e.g. "a/b" is ancestor of "a/b/c" but not of "a/b" itself).
	// Root is ancestor of every non-root path. Used for include validation (spec §1, §4.4) and for
	// API-key RootPath scope checks (§8).
	public bool IsAncestorOf(NodePath other)
	{
		if (IsRoot)
			return !other.IsRoot;
		var self = canonical!;
		var target = other.canonical;
		if (string.IsNullOrEmpty(target) || target.Length <= self.Length)
			return false;
		return target.AsSpan(0, self.Length).SequenceEqual(self) && target[self.Length] == '/';
	}

	// Inverse of IsAncestorOf.
	public bool IsDescendantOf(NodePath other) => other.IsAncestorOf(this);

	public static NodePath ParseDb(string path) => Parse(path, '/');

	public static NodePath ParseUrl(string urlPath) => Parse(urlPath, '.');

	// Slug per spec §8: non-system segments match `^[a-z0-9][a-z0-9-]{1,39}$`,
	// optional `$` prefix reserves system nodes ($system, $bootstrap).
	[GeneratedRegex(@"^\$?[a-z0-9][a-z0-9-]{1,39}$")]
	private static partial Regex SegmentRegex();

	static NodePath Parse(string path, char separator)
	{
		if (string.IsNullOrEmpty(path))
			return Root;
		var segments = path.Split(separator);
		foreach (var seg in segments)
		{
			if (!SegmentRegex().IsMatch(seg))
				throw new ArgumentException(
					$"Invalid path segment '{seg}' in '{path}': must match ^\\$?[a-z0-9][a-z0-9-]{{1,39}}$",
					nameof(path));
		}
		return new NodePath(separator == '/' ? path : string.Join('/', segments));
	}
}
