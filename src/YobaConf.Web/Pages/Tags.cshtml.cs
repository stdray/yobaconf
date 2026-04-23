using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Tags;

namespace YobaConf.Web.Pages;

public sealed class TagsModel : PageModel
{
	readonly ITagVocabularyStore _store;
	readonly ITagVocabularyAdmin _admin;
	readonly TimeProvider _clock;

	public TagsModel(ITagVocabularyStore store, ITagVocabularyAdmin admin, TimeProvider clock)
	{
		_store = store;
		_admin = admin;
		_clock = clock;
	}

	public IReadOnlyList<IGrouping<string, TagVocabularyEntry>> Groups { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? SuccessMessage { get; set; }

	public void OnGet() => Load();

	public IActionResult OnPostCreate(string? key, string? value, string? description, int? priority)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			ErrorMessage = "Tag key is required.";
			Load();
			return Page();
		}

		try
		{
			var clampedPriority = Math.Max(0, priority ?? 0);
			var entry = _admin.Create(key, value, description, clampedPriority, _clock.GetUtcNow(), User.Identity?.Name ?? "system");
			SuccessMessage = entry.Value is null
				? $"Declared tag key '{entry.Key}'."
				: $"Declared '{entry.Key}={entry.Value}'.";
		}
		catch (InvalidOperationException ex)
		{
			ErrorMessage = ex.Message;
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = ex.Message;
		}

		Load();
		return Page();
	}

	public IActionResult OnPostDelete(long? id)
	{
		if (id is null)
		{
			ErrorMessage = "Missing tag id.";
			Load();
			return Page();
		}

		if (!_admin.SoftDelete(id.Value, _clock.GetUtcNow(), User.Identity?.Name ?? "system"))
			ErrorMessage = $"Tag #{id.Value} not found.";

		Load();
		return Page();
	}

	void Load() =>
		Groups = [.. _store.ListActive().GroupBy(e => e.Key).OrderBy(g => g.Key)];
}
