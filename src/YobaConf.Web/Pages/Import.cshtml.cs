using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;
using YobaConf.Core.Converters;
using YobaConf.Core.Security;

namespace YobaConf.Web.Pages;

// Phase B.7 — two-step import with per-leaf classification (Keep / Variable / Secret).
// Step 1 (paste): source + format + path, [Convert] shows HOCON preview, [Classify] (only
// enabled for flat HOCON — no nested {} or []) moves to Step 2.
// Step 2 (classify): table of top-level leaves. User picks Keep/Variable/Secret per row;
// values masked by default with per-row client-side Reveal. Save applies the split:
// classified-Variable leaves go into Variables table, classified-Secret encrypted + into
// Secrets table, Keep leaves stay in the raw HOCON of the saved Node.
//
// MVP limitations (documented in plan.md):
//   * Only flat HOCON eligible for classify; nested goes through single-node save path.
//   * Plaintext values are present in the classify-step HTML (hidden inputs). Moving to
//     server-side MemoryCache with a reveal endpoint is a follow-up — same pattern as
//     /Node secret-reveal but gated on a short-lived cache token.
[IgnoreAntiforgeryToken]
public sealed partial class ImportModel : PageModel
{
	readonly IConfigStoreAdmin _admin;
	readonly ISecretEncryptor? _encryptor;
	readonly TimeProvider _clock;

	public ImportModel(IConfigStoreAdmin admin, TimeProvider clock, ISecretEncryptor? encryptor = null)
	{
		_admin = admin;
		_clock = clock;
		_encryptor = encryptor;
	}

	[BindProperty] public string? TargetPath { get; set; }
	[BindProperty] public string Format { get; set; } = "json";
	[BindProperty] public string? Source { get; set; }
	[BindProperty] public string? Action { get; set; }

	// Step-2 state carried through the classify form.
	[BindProperty] public string? PreviewHocon { get; set; }
	[BindProperty] public List<ClassifyLeaf> Leaves { get; set; } = [];

	public string Preview { get; private set; } = string.Empty;
	public string? ErrorMessage { get; private set; }
	public string? SuccessMessage { get; private set; }
	public bool ClassifyStep { get; private set; }
	public bool IsFlat { get; private set; }

	public void OnGet() { }

	// Step 1 — paste + Convert / Classify / Save-as-single-node.
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
		IsFlat = IsFlatHocon(hocon);

		switch (Action)
		{
			case "classify":
				if (!IsFlat)
				{
					ErrorMessage = "Classification is only available for flat HOCON (no nested objects/arrays). Use 'Save' to store as a single node instead.";
					return Page();
				}
				Leaves = [.. ExtractLeaves(hocon).Select(kv => new ClassifyLeaf { Key = kv.Key, Value = kv.Value, Classification = "keep" })];
				PreviewHocon = hocon;
				ClassifyStep = true;
				return Page();

			case "save":
				return SaveSingle(hocon);

			default: // "convert" (preview only)
				return Page();
		}
	}

	// Step 2 — classify Save: split into RawContent + Variables + Secrets and commit all.
	public IActionResult OnPostSaveClassified()
	{
		if (string.IsNullOrWhiteSpace(TargetPath) || string.IsNullOrEmpty(PreviewHocon))
		{
			ErrorMessage = "Classification state missing; restart the flow.";
			return Page();
		}

		NodePath path;
		try { path = NodePath.ParseUrl(TargetPath); }
		catch (ArgumentException ex) { ErrorMessage = $"Invalid target path: {ex.Message}"; return Page(); }

		if (path.IsRoot)
		{
			ErrorMessage = "Cannot save at the root path.";
			return Page();
		}

		var keepLines = new StringBuilder();
		var extracted = new List<(string Key, string Value, string Kind)>();
		var keysByClassification = Leaves.ToDictionary(l => l.Key, l => l.Classification, StringComparer.Ordinal);

		foreach (var (key, value) in ExtractLeaves(PreviewHocon))
		{
			var klass = keysByClassification.TryGetValue(key, out var k) ? k : "keep";
			if (klass == "keep")
				keepLines.AppendLine($"{key} = {value}");
			else
				extracted.Add((key, UnquoteHoconValue(value), klass));
		}

		var actor = User.Identity?.Name ?? "admin";
		var now = _clock.GetUtcNow();

		_admin.UpsertNode(path, keepLines.ToString().TrimEnd(), now, actor);

		foreach (var (key, value, kind) in extracted)
		{
			if (kind == "variable")
			{
				_admin.UpsertVariable(path, key, value, now, actor);
			}
			else // secret
			{
				if (_encryptor is null)
				{
					ErrorMessage = $"Cannot classify '{key}' as Secret: YOBACONF_MASTER_KEY not configured.";
					return Page();
				}
				var bundle = _encryptor.Encrypt(value);
				_admin.UpsertSecret(path, key, bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, now, actor);
			}
		}

		SuccessMessage = $"Saved '{path.ToDbPath()}' ({extracted.Count(e => e.Kind == "variable")} variable(s), {extracted.Count(e => e.Kind == "secret")} secret(s) extracted).";
		TargetPath = null;
		Source = null;
		Leaves = [];
		PreviewHocon = null;
		return Page();
	}

	PageResult SaveSingle(string hocon)
	{
		if (string.IsNullOrWhiteSpace(TargetPath))
		{
			ErrorMessage = "Target path is required to save.";
			Preview = hocon;
			return Page();
		}
		NodePath path;
		try { path = NodePath.ParseUrl(TargetPath); }
		catch (ArgumentException ex) { ErrorMessage = $"Invalid target path: {ex.Message}"; Preview = hocon; return Page(); }

		if (path.IsRoot)
		{
			ErrorMessage = "Cannot save at the root path.";
			Preview = hocon;
			return Page();
		}

		_admin.UpsertNode(path, hocon, _clock.GetUtcNow(), actor: User.Identity?.Name ?? "admin");
		SuccessMessage = $"Saved as '{path.ToDbPath()}'.";
		TargetPath = null;
		Source = null;
		return Page();
	}

	static bool IsFlatHocon(string hocon) =>
		!hocon.Contains('{', StringComparison.Ordinal) && !hocon.Contains('[', StringComparison.Ordinal);

	// Parse `key = value` / `key = "value"` lines out of flat HOCON. Skips comments and
	// blank lines. Multi-line values (triple-quoted) are not expected in flat-HOCON MVP;
	// if encountered they're treated as opaque-per-first-line (fine for .env-origin imports
	// where that shape doesn't occur).
	internal static IEnumerable<KeyValuePair<string, string>> ExtractLeaves(string hocon)
	{
		foreach (var line in hocon.Split('\n'))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
				continue;
			var m = LeafLineRegex().Match(trimmed);
			if (m.Success)
				yield return new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value.TrimEnd());
		}
	}

	// Strip surrounding quotes from a HOCON value (single layer only — no escape handling
	// in MVP, which matches what converters emit for simple strings).
	internal static string UnquoteHoconValue(string v)
	{
		v = v.Trim();
		if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
			return v.Substring(1, v.Length - 2);
		return v;
	}

	// Matches `key = value` or `"key" = "value"` (the latter is what dotenv-converter emits).
	// Captures the unquoted key and the raw right-hand side (value quotes, if any, are
	// stripped at Save time via UnquoteHoconValue so classify → Variable/Secret stores the
	// plaintext, not the quoted form).
	[GeneratedRegex(@"^""?([A-Za-z_][A-Za-z0-9_\-]*)""?\s*=\s*(.+)$")]
	private static partial Regex LeafLineRegex();
}

public sealed class ClassifyLeaf
{
	public string Key { get; set; } = string.Empty;
	public string Value { get; set; } = string.Empty;
	public string Classification { get; set; } = "keep";  // "keep" | "variable" | "secret"
}
