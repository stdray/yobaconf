using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;
using YobaConf.Core.Include;

namespace YobaConf.Web.Pages;

public sealed class NodeModel : PageModel
{
	readonly IConfigStore _store;

	public NodeModel(IConfigStore store) => _store = store;

	public NodePath Path { get; private set; }
	public bool NodeExists { get; private set; }
	public string RawContent { get; private set; } = string.Empty;
	public NodePath? ResolvedFromPath { get; private set; }

	public string ResolvedJson { get; private set; } = string.Empty;
	public string ETag { get; private set; } = string.Empty;
	public string? ResolveError { get; private set; }

	public IActionResult OnGet(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return RedirectToPage("/Index");

		try
		{
			Path = NodePath.ParseUrl(path);
		}
		catch (ArgumentException)
		{
			return BadRequest();
		}

		var exact = _store.FindNode(Path);
		NodeExists = exact is not null;
		RawContent = exact?.RawContent ?? string.Empty;

		try
		{
			var result = ResolvePipeline.Resolve(Path, _store);
			ResolvedJson = PrettyPrintJson(result.Json);
			ETag = result.ETag;

			if (!NodeExists)
			{
				// Fallthrough landed us somewhere else — show the user where.
				var best = NodeResolver.FindBestMatch(_store, Path);
				if (best is not null && !best.Path.Equals(Path))
					ResolvedFromPath = best.Path;
			}
		}
		catch (NodeNotFoundException)
		{
			ResolveError = "No node exists at this path or any of its ancestors.";
		}
		catch (Hocon.HoconParserException ex)
		{
			ResolveError = $"HOCON parse error: {ex.Message}";
		}
		catch (IncludeException ex)
		{
			ResolveError = $"Include resolution failed: {ex.Message}";
		}

		return Page();
	}

	// ResolvePipeline emits compact JSON (good for ETag stability and HTTP responses).
	// For the admin preview we re-format with indentation so it's readable in the UI.
	// Pretty-printing doesn't affect the ETag — that's already computed from the compact
	// form upstream.
	static string PrettyPrintJson(string compact)
	{
		using var doc = JsonDocument.Parse(compact);
		return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
	}

	static readonly JsonSerializerOptions PrettyOptions = new()
	{
		WriteIndented = true,
		IndentSize = 2,
	};
}
