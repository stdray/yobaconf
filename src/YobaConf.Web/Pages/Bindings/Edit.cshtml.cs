using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;
using YobaConf.Core.Tags;

namespace YobaConf.Web.Pages.Bindings;

public sealed class EditModel : PageModel
{
	readonly IBindingStore _store;
	readonly IBindingStoreAdmin _admin;
	readonly ISecretEncryptor? _encryptor;
	readonly ITagVocabularyStore? _vocabulary;
	readonly TimeProvider _clock;

	public EditModel(
		IBindingStore store,
		IBindingStoreAdmin admin,
		TimeProvider clock,
		ISecretEncryptor? encryptor = null,
		ITagVocabularyStore? vocabulary = null)
	{
		_store = store;
		_admin = admin;
		_clock = clock;
		_encryptor = encryptor;
		_vocabulary = vocabulary;
	}

	public long? BindingId { get; private set; }
	public bool IsCreate => BindingId is null or 0;
	public string TagsText { get; private set; } = string.Empty;
	public string KeyPath { get; private set; } = string.Empty;
	public string ValueText { get; private set; } = string.Empty;
	public BindingKind Kind { get; private set; } = BindingKind.Plain;
	public string? ErrorMessage { get; set; }
	public string? ConflictMessage { get; set; }
	public IReadOnlyList<string> UnknownTagKeys { get; private set; } = [];

	public IActionResult OnGet(long? id)
	{
		BindingId = id;
		if (id is null or 0) return Page();

		var existing = _store.FindById(id.Value);
		if (existing is null || existing.IsDeleted)
		{
			ErrorMessage = $"Binding {id} not found.";
			return Page();
		}

		TagsText = string.Join('\n', existing.TagSet.Select(kv => $"{kv.Key}={kv.Value}"));
		KeyPath = existing.KeyPath;
		Kind = existing.Kind;

		// For edit-of-Plain we show the stored JSON-encoded value. For edit-of-Secret the
		// UI starts empty — operator must type a new plaintext to rotate; we never
		// decrypt-then-render in a writable field (would leak into the DOM on validation
		// bounce).
		ValueText = existing.Kind == BindingKind.Plain ? (existing.ValuePlain ?? string.Empty) : string.Empty;
		return Page();
	}

	public IActionResult OnPost(long? id, string? tags, string? keyPath, string? value, string? kind)
	{
		BindingId = id;
		TagsText = tags ?? string.Empty;
		KeyPath = keyPath?.Trim() ?? string.Empty;
		ValueText = value ?? string.Empty;
		Kind = string.Equals(kind, "Secret", StringComparison.Ordinal) ? BindingKind.Secret : BindingKind.Plain;

		TagSet tagSet;
		try
		{
			tagSet = ParseTagSet(TagsText);
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = $"Tags: {ex.Message}";
			return Page();
		}

		try
		{
			Slug.RequireKeyPath(KeyPath);
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = ex.Message;
			return Page();
		}

		if (string.IsNullOrWhiteSpace(ValueText))
		{
			ErrorMessage = "Value is required.";
			return Page();
		}

		var now = _clock.GetUtcNow();
		Binding candidate;
		if (Kind == BindingKind.Secret)
		{
			if (_encryptor is null)
			{
				ErrorMessage = "Secret bindings require YOBACONF_MASTER_KEY to be configured.";
				return Page();
			}
			var bundle = _encryptor.Encrypt(ValueText);
			candidate = new Binding
			{
				Id = 0,
				TagSet = tagSet,
				KeyPath = KeyPath,
				Kind = BindingKind.Secret,
				Ciphertext = bundle.Ciphertext,
				Iv = bundle.Iv,
				AuthTag = bundle.AuthTag,
				KeyVersion = bundle.KeyVersion,
				ContentHash = string.Empty,
				UpdatedAt = now,
			};
		}
		else
		{
			candidate = new Binding
			{
				Id = 0,
				TagSet = tagSet,
				KeyPath = KeyPath,
				Kind = BindingKind.Plain,
				ValuePlain = ValueText,
				ContentHash = string.Empty,
				UpdatedAt = now,
			};
		}

		var outcome = _admin.Upsert(candidate, User.Identity?.Name ?? "system");

		// Post-save advisories — both are non-blocking (save already committed). Stay on
		// page so the admin sees them before navigating away.
		ConflictMessage = DetectConflict(outcome.Binding);
		UnknownTagKeys = DetectUnknownTagKeys(tagSet);

		if (ConflictMessage is not null || UnknownTagKeys.Count > 0)
		{
			BindingId = outcome.Binding.Id;
			return Page();
		}

		return RedirectToPage("/Bindings/Index");
	}

	IReadOnlyList<string> DetectUnknownTagKeys(TagSet tagSet)
	{
		// Empty vocabulary = opt-in / free-form mode. No warnings until the first
		// declaration lands in /Tags.
		if (_vocabulary is null) return [];
		var known = _vocabulary.DistinctKeys();
		if (known.Count == 0) return [];

		var set = new HashSet<string>(known, StringComparer.Ordinal);
		return [.. tagSet.Select(kv => kv.Key).Where(k => !set.Contains(k)).Distinct(StringComparer.Ordinal)];
	}

	string? DetectConflict(Binding saved)
	{
		// Scan every active binding with same KeyPath + same TagCount. If any of them has
		// TagSet incomparable to `saved.TagSet` (neither a subset of the other), it's a
		// conflict-risk overlay the admin should know about.
		foreach (var other in _store.ListActive())
		{
			if (other.Id == saved.Id) continue;
			if (!string.Equals(other.KeyPath, saved.KeyPath, StringComparison.Ordinal)) continue;
			if (other.TagSet.Count != saved.TagSet.Count) continue;
			if (AreIncomparable(saved.TagSet, other.TagSet))
				return $"Binding #{other.Id} at {other.TagSet.CanonicalJson} has the same KeyPath and specificity. " +
					"Resolves matching both tag-sets will 409. Add a more-specific overlay to disambiguate.";
		}
		return null;
	}

	static bool AreIncomparable(TagSet a, TagSet b)
	{
		// Same TagCount → subset-check must be strict both ways. If neither is subset of
		// the other they're incomparable.
		var aDict = a.ToDictionary(kv => kv.Key, kv => kv.Value);
		var bDict = b.ToDictionary(kv => kv.Key, kv => kv.Value);
		return !a.IsSubsetOf(bDict) && !b.IsSubsetOf(aDict);
	}

	static TagSet ParseTagSet(string raw)
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
}
