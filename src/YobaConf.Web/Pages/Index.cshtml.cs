using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;

namespace YobaConf.Web.Pages;

// Tree-view Index. Flat `ListNodePaths()` gets projected into a hierarchical `TreeNode[]`
// for Razor rendering — the grouping logic lives in code so snapshot assertions have a
// stable shape to target and the Razor template can stay dumb.
//
// Virtual directories (`TreeNode.IsVirtual = true`) are path prefixes where no stored node
// exists but descendants do (e.g. `animemov-bot/` with only `animemov-bot/prod` stored).
// They render with an open dot (○) vs filled (●) actual nodes.
public sealed class IndexModel : PageModel
{
	readonly IConfigStore _store;
	readonly IConfigStoreAdmin _admin;
	readonly TimeProvider _clock;

	public IndexModel(IConfigStore store, IConfigStoreAdmin admin, TimeProvider clock)
	{
		_store = store;
		_admin = admin;
		_clock = clock;
	}

	public IReadOnlyList<TreeNode> Tree { get; private set; } = [];
	public int ActualNodeCount { get; private set; }
	public int VirtualDirCount { get; private set; }
	public string? ErrorMessage { get; private set; }

	public void OnGet()
	{
		Build();
	}

	public IActionResult OnPostNewEmpty(string? newPath)
	{
		if (string.IsNullOrWhiteSpace(newPath))
		{
			ErrorMessage = "Enter a path for the new node (e.g. `project-a.prod`).";
			Build();
			return Page();
		}

		NodePath path;
		try
		{
			// UI uses dotted form; parse both dotted and slashed shapes defensively.
			path = newPath.Contains('/', StringComparison.Ordinal)
				? NodePath.ParseDb(newPath)
				: NodePath.ParseUrl(newPath);
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = $"Invalid path: {ex.Message}";
			Build();
			return Page();
		}

		if (path.Equals(NodePath.Root))
		{
			ErrorMessage = "Cannot create a node at the root path.";
			Build();
			return Page();
		}

		if (_store.FindNode(path) is not null)
		{
			return RedirectToPage("/Node", new { path = path.ToUrlPath() });
		}

		// Empty-node creation writes a benign empty object — Hocon.ParseString chokes on
		// literal whitespace, so the resolve pipeline normalises "" to "{}" anyway. Storing
		// an empty string is fine; the user edits it after redirect.
		_admin.UpsertNode(path, string.Empty, _clock.GetUtcNow(), actor: User.Identity?.Name ?? "admin");
		return RedirectToPage("/Node", new { path = path.ToUrlPath() });
	}

	void Build()
	{
		var paths = _store.ListNodePaths();
		ActualNodeCount = paths.Count;
		Tree = BuildTree(paths);
		VirtualDirCount = Tree.Count(HasVirtualDescendants) + Tree.Sum(CountVirtualDescendants);
	}

	static bool HasVirtualDescendants(TreeNode n) => n.IsVirtual || n.Children.Any(HasVirtualDescendants);

	static int CountVirtualDescendants(TreeNode n) =>
		n.Children.Sum(c => (c.IsVirtual ? 1 : 0) + CountVirtualDescendants(c));

	// Fold a flat sorted path list into a hierarchy. Each path contributes leaf nodes at
	// each segment depth; intermediate segments without a backing store entry show up as
	// virtual dirs. Preserves sibling order by descending input order (ListNodePaths is
	// already ordinal-sorted).
	internal static IReadOnlyList<TreeNode> BuildTree(IReadOnlyList<NodePath> paths)
	{
		var root = new Dictionary<string, TreeBuilder>(StringComparer.Ordinal);
		foreach (var p in paths)
		{
			var segments = p.ToDbPath().Split('/');
			Dictionary<string, TreeBuilder> cursor = root;
			for (var i = 0; i < segments.Length; i++)
			{
				var seg = segments[i];
				if (!cursor.TryGetValue(seg, out var node))
				{
					node = new TreeBuilder { Segment = seg };
					cursor[seg] = node;
				}
				if (i == segments.Length - 1)
				{
					node.Path = p;
					node.IsActual = true;
				}
				cursor = node.Children;
			}
		}
		return Materialize(root);
	}

	static IReadOnlyList<TreeNode> Materialize(Dictionary<string, TreeBuilder> level) =>
		[.. level
			.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => new TreeNode(
				kv.Value.Segment,
				kv.Value.Path,
				IsVirtual: !kv.Value.IsActual,
				Materialize(kv.Value.Children)))];

	sealed class TreeBuilder
	{
		public string Segment { get; set; } = string.Empty;
		public NodePath? Path { get; set; }
		public bool IsActual { get; set; }
		public Dictionary<string, TreeBuilder> Children { get; } = new(StringComparer.Ordinal);
	}
}

// Value object consumed by Index.cshtml. `Path` is non-null iff `IsVirtual == false` —
// the Razor template walks children regardless.
public sealed record TreeNode(string Segment, NodePath? Path, bool IsVirtual, IReadOnlyList<TreeNode> Children);

// Context carried through recursive _TreeNode.cshtml partial invocations.
public sealed record TreeNodeContext(IReadOnlyList<TreeNode> Nodes, int Depth);
