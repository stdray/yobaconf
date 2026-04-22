using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;
using YobaConf.Core.Converters;

namespace YobaConf.Web.Pages;

// Antiforgery off for Phase A — see Login.cshtml.cs for the rationale. Phase B's admin
// CRUD enables it across all mutating pages.
[IgnoreAntiforgeryToken]
public sealed class ImportModel : PageModel
{
	readonly IConfigStoreAdmin _admin;
	readonly TimeProvider _clock;

	public ImportModel(IConfigStoreAdmin admin, TimeProvider clock)
	{
		_admin = admin;
		_clock = clock;
	}

	[BindProperty] public string? TargetPath { get; set; }
	[BindProperty] public string Format { get; set; } = "json";
	[BindProperty] public string? Source { get; set; }
	[BindProperty] public string? Action { get; set; }

	public string Preview { get; private set; } = string.Empty;
	public string? ErrorMessage { get; private set; }
	public string? SuccessMessage { get; private set; }

	public void OnGet()
	{
	}

	public IActionResult OnPost()
	{
		if (string.IsNullOrWhiteSpace(Source))
		{
			ErrorMessage = "Source text is empty. Paste a JSON / YAML / .env document.";
			return Page();
		}

		string hocon;
		try
		{
			hocon = Format switch
			{
				"json" => JsonToHoconConverter.Convert(Source),
				"yaml" => YamlToHoconConverter.Convert(Source),
				"env" => DotenvToHoconConverter.Convert(Source),
				_ => throw new ImportException($"Unknown format: '{Format}'"),
			};
		}
		catch (ImportException ex)
		{
			ErrorMessage = ex.Message;
			return Page();
		}

		Preview = hocon;

		if (Action == "save")
		{
			if (string.IsNullOrWhiteSpace(TargetPath))
			{
				ErrorMessage = "Target path is required to save.";
				return Page();
			}
			NodePath path;
			try
			{
				path = NodePath.ParseUrl(TargetPath);
			}
			catch (ArgumentException ex)
			{
				ErrorMessage = $"Invalid target path: {ex.Message}";
				return Page();
			}
			if (path.IsRoot)
			{
				// Phase A doesn't expose a way to fetch/serve root — root-node storage is
				// supported by the DB but we'd need extra UI affordance. Reject explicitly
				// so operators don't silently create something they can't easily see.
				ErrorMessage = "Cannot save at the root path in Phase A UI. Pick a named path.";
				return Page();
			}

			_admin.UpsertNode(path, hocon, _clock.GetUtcNow(), actor: User.Identity?.Name ?? "admin");
			SuccessMessage = $"Saved as '{path.ToDbPath()}'.";
			TargetPath = null;
			Source = null;
		}

		return Page();
	}
}
