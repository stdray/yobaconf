using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Web.Pages.Admin;

public sealed class ApiKeysModel : PageModel
{
	readonly IApiKeyAdmin _admin;
	readonly TimeProvider _clock;

	public ApiKeysModel(IApiKeyAdmin admin, TimeProvider clock)
	{
		_admin = admin;
		_clock = clock;
	}

	public IReadOnlyList<ApiKeyInfo> Keys { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? NewlyCreatedToken { get; set; }

	public void OnGet() => Load();

	public IActionResult OnPostCreate(string? description, string? requiredTags, string? allowedPrefixes)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			ErrorMessage = "Description is required.";
			Load();
			return Page();
		}

		TagSet tagSet;
		try
		{
			tagSet = ParseRequiredTags(requiredTags);
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = $"Required tags: {ex.Message}";
			Load();
			return Page();
		}

		var prefixes = ParsePrefixes(allowedPrefixes);

		var created = _admin.Create(tagSet, prefixes, description.Trim(), _clock.GetUtcNow());
		NewlyCreatedToken = created.Plaintext;
		Load();
		return Page();
	}

	public IActionResult OnPostDelete(long? id)
	{
		if (id is null)
		{
			ErrorMessage = "Missing key id.";
			Load();
			return Page();
		}

		if (!_admin.SoftDelete(id.Value, _clock.GetUtcNow()))
			ErrorMessage = $"Key {id.Value} not found.";

		Load();
		return Page();
	}

	void Load() => Keys = _admin.ListActive();

	static TagSet ParseRequiredTags(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return TagSet.Empty;

		var pairs = new List<KeyValuePair<string, string>>();
		foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0) continue;
			var eq = trimmed.IndexOf('=');
			if (eq <= 0 || eq == trimmed.Length - 1)
				throw new ArgumentException($"'{trimmed}' is not a 'key=value' pair.");
			pairs.Add(new(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
		}
		return TagSet.From(pairs);
	}

	static string[]? ParsePrefixes(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Select(l => l.Trim())
			.Where(l => l.Length > 0)
			.ToArray();
		return lines.Length == 0 ? null : lines;
	}
}
