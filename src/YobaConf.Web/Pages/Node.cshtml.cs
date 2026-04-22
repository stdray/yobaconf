using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;
using YobaConf.Core.Include;
using YobaConf.Core.Security;

namespace YobaConf.Web.Pages;

// Phase B: Variables + Secrets inline CRUD on the Node page. Each row renders as its own
// form (Save + Delete buttons via `formaction` override) — no JS-driven inline edit, plain
// POST-redirect-GET flow. CSRF is Phase-B-and-later; antiforgery stays on.
//
// Reveal contract: Secret plaintext is NEVER in the initial HTML (preview uses "(secret)"
// plaintext substitute for any top-level key matching a stored Secret key at this scope
// chain). Reveal is a separate GET returning just the decrypted value as plain text — the
// page surfaces it via inline JS on a per-row click, auto-hides after 10s.
[IgnoreAntiforgeryToken]
public sealed class NodeModel : PageModel
{
	readonly IConfigStore _store;
	readonly IConfigStoreAdmin _admin;
	readonly ISecretEncryptor? _encryptor;
	readonly TimeProvider _clock;

	public NodeModel(IConfigStore store, IConfigStoreAdmin admin, TimeProvider clock, ISecretEncryptor? encryptor = null)
	{
		_store = store;
		_admin = admin;
		_clock = clock;
		_encryptor = encryptor;
	}

	public NodePath Path { get; private set; }
	public bool NodeExists { get; private set; }
	public string RawContent { get; private set; } = string.Empty;
	public string RawContentHash { get; private set; } = string.Empty;
	public NodePath? ResolvedFromPath { get; private set; }

	public string ResolvedJson { get; private set; } = string.Empty;
	public string ETag { get; private set; } = string.Empty;
	public string? ResolveError { get; private set; }

	public IReadOnlyList<Variable> Variables { get; private set; } = [];
	public IReadOnlyList<Secret> Secrets { get; private set; } = [];

	public string? ErrorMessage { get; private set; }
	public string? SuccessMessage { get; private set; }
	public string? ConflictMessage { get; private set; }
	public bool EditMode { get; private set; }
	public string? RevealKey { get; private set; }
	public string? RevealValue { get; private set; }

	public IActionResult OnGet(string? path, string? reveal, bool edit = false)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		EditMode = edit;
		Load();

		if (!string.IsNullOrEmpty(reveal))
		{
			var target = Secrets.FirstOrDefault(s => s.Key == reveal);
			if (target is not null && _encryptor is not null)
			{
				RevealKey = target.Key;
				RevealValue = _encryptor.Decrypt(target.EncryptedValue, target.Iv, target.AuthTag, target.KeyVersion);
			}
		}

		return Page();
	}

	public IActionResult OnPostCreateEmpty(string? path)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		if (parsed.Equals(NodePath.Root))
		{
			TempData["Error"] = "Cannot create a node at the root path.";
			return RedirectPostGet();
		}
		if (_store.FindNode(parsed) is not null)
		{
			// Already exists — race or user clicked twice. Just navigate.
			return RedirectPostGet();
		}
		_admin.UpsertNode(parsed, string.Empty, _clock.GetUtcNow(), Actor());
		TempData["Success"] = "Created empty node. Click Edit to add content.";
		return RedirectPostGet();
	}

	public IActionResult OnPostUpdateNode(string? path, string? rawContent, string? expectedHash)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		rawContent ??= string.Empty;

		var outcome = _admin.UpsertNode(parsed, rawContent, _clock.GetUtcNow(), Actor(), expectedHash);
		if (outcome == UpsertOutcome.Conflict)
		{
			// Preserve the user's in-flight edit — re-render edit mode with a conflict bar,
			// don't discard their text. They can copy-paste out and reload if they want to
			// see what changed.
			EditMode = true;
			RawContent = rawContent;
			ConflictMessage = "This node was modified in another session since you started editing. Copy your changes, reload the page, and re-apply.";
			Load(skipStoreRead: true);
			return Page();
		}

		TempData["Success"] = "Saved HOCON content.";
		return RedirectPostGet();
	}

	public IActionResult OnPostAddVariable(string? path, string? newKey, string? newValue)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;

		if (string.IsNullOrWhiteSpace(newKey) || newValue is null)
		{
			ErrorMessage = "Variable key and value are required.";
			Load();
			return Page();
		}

		var outcome = _admin.UpsertVariable(parsed, newKey, newValue, _clock.GetUtcNow(), Actor());
		if (outcome == UpsertOutcome.Conflict)
			ErrorMessage = $"Variable '{newKey}' already exists at this scope; edit the row instead.";
		else
			SuccessMessage = $"Added variable '{newKey}'.";
		return RedirectPostGet();
	}

	public IActionResult OnPostUpdateVariable(string? path, string? key, string? value, string? expectedHash)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		if (string.IsNullOrWhiteSpace(key) || value is null)
			return RedirectPostGet();

		var outcome = _admin.UpsertVariable(parsed, key, value, _clock.GetUtcNow(), Actor(), expectedHash);
		if (outcome == UpsertOutcome.Conflict)
			TempData["Error"] = $"Variable '{key}' changed in another session; reload and retry.";
		else
			TempData["Success"] = $"Saved variable '{key}'.";
		return RedirectPostGet();
	}

	public IActionResult OnPostDeleteVariable(string? path, string? key, string? expectedHash)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		if (string.IsNullOrWhiteSpace(key)) return RedirectPostGet();

		var outcome = _admin.SoftDeleteVariable(parsed, key, Actor(), expectedHash);
		if (outcome == UpsertOutcome.Conflict)
			TempData["Error"] = $"Variable '{key}' could not be deleted (changed or gone); reload and retry.";
		else
			TempData["Success"] = $"Deleted variable '{key}'.";
		return RedirectPostGet();
	}

	public IActionResult OnPostAddSecret(string? path, string? newKey, string? newValue)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;

		if (_encryptor is null)
		{
			TempData["Error"] = "Secret encryption not configured (YOBACONF_MASTER_KEY).";
			return RedirectPostGet();
		}
		if (string.IsNullOrWhiteSpace(newKey) || string.IsNullOrEmpty(newValue))
		{
			TempData["Error"] = "Secret key and value are required.";
			return RedirectPostGet();
		}

		var bundle = _encryptor.Encrypt(newValue);
		var outcome = _admin.UpsertSecret(parsed, newKey, bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, _clock.GetUtcNow(), Actor());
		if (outcome == UpsertOutcome.Conflict)
			TempData["Error"] = $"Secret '{newKey}' already exists at this scope.";
		else
			TempData["Success"] = $"Added secret '{newKey}'.";
		return RedirectPostGet();
	}

	public IActionResult OnPostUpdateSecret(string? path, string? key, string? value, string? expectedHash)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		if (_encryptor is null)
		{
			TempData["Error"] = "Secret encryption not configured (YOBACONF_MASTER_KEY).";
			return RedirectPostGet();
		}
		if (string.IsNullOrWhiteSpace(key) || value is null) return RedirectPostGet();

		var bundle = _encryptor.Encrypt(value);
		var outcome = _admin.UpsertSecret(parsed, key, bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, _clock.GetUtcNow(), Actor(), expectedHash);
		if (outcome == UpsertOutcome.Conflict)
			TempData["Error"] = $"Secret '{key}' changed in another session; reload and retry.";
		else
			TempData["Success"] = $"Saved secret '{key}'.";
		return RedirectPostGet();
	}

	public IActionResult OnPostDeleteSecret(string? path, string? key, string? expectedHash)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		Path = parsed;
		if (string.IsNullOrWhiteSpace(key)) return RedirectPostGet();

		var outcome = _admin.SoftDeleteSecret(parsed, key, Actor(), expectedHash);
		if (outcome == UpsertOutcome.Conflict)
			TempData["Error"] = $"Secret '{key}' could not be deleted; reload and retry.";
		else
			TempData["Success"] = $"Deleted secret '{key}'.";
		return RedirectPostGet();
	}

	// GET endpoint returning plaintext secret value. Lives under the Node page so the
	// existing cookie-auth fallback policy applies; anon callers get redirected to /Login.
	// 10-second auto-hide is client-side (see node.js below); this endpoint is the only
	// server-side source of the decrypted value.
	public IActionResult OnGetRevealSecret(string? path, string? key)
	{
		if (!TryParse(path, out var parsed)) return BadRequest();
		if (string.IsNullOrWhiteSpace(key)) return BadRequest();
		if (_encryptor is null) return StatusCode(503);

		var secret = _store.FindSecrets(parsed).FirstOrDefault(s => s.Key == key && !s.IsDeleted);
		if (secret is null) return NotFound();

		var plain = _encryptor.Decrypt(secret.EncryptedValue, secret.Iv, secret.AuthTag, secret.KeyVersion);
		return Content(plain, "text/plain; charset=utf-8");
	}

	void Load(bool skipStoreRead = false)
	{
		var exact = _store.FindNode(Path);
		NodeExists = exact is not null;
		if (!skipStoreRead)
			RawContent = exact?.RawContent ?? string.Empty;
		RawContentHash = exact?.ContentHash ?? string.Empty;
		Variables = _store.FindVariables(Path).Where(v => !v.IsDeleted).OrderBy(v => v.Key, StringComparer.Ordinal).ToArray();
		Secrets = _store.FindSecrets(Path).Where(s => !s.IsDeleted).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();

		if (TempData["Error"] is string err) ErrorMessage = err;
		if (TempData["Success"] is string ok) SuccessMessage = ok;

		try
		{
			var result = ResolvePipeline.Resolve(Path, _store, _encryptor);
			ResolvedJson = PrettyPrintJson(RedactSecrets(result.Json, Path));
			ETag = result.ETag;

			if (!NodeExists)
			{
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
	}

	// Collect every Secret key in this path's scope chain (self + ancestors) and blank
	// them out in the resolved JSON preview. Phase-B limitation: only top-level keys are
	// redacted. A secret referenced as `db.password = ${db_password}` in HOCON produces
	// `db.password` nested — we don't track which keys came from which layer, so nested
	// refs show plaintext. Documented in doc/ui-reference.md.
	string RedactSecrets(string json, NodePath path)
	{
		var secretKeys = new HashSet<string>(StringComparer.Ordinal);
		for (NodePath? cur = path; cur is not null; cur = cur.Value.Parent)
		{
			foreach (var s in _store.FindSecrets(cur.Value))
				if (!s.IsDeleted) secretKeys.Add(s.Key);
		}
		if (secretKeys.Count == 0) return json;

		var node = JsonNode.Parse(json);
		if (node is not JsonObject obj) return json;

		foreach (var key in secretKeys)
		{
			if (obj.ContainsKey(key))
				obj[key] = JsonValue.Create("(secret)");
		}
		return obj.ToJsonString();
	}

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

	static bool TryParse(string? path, out NodePath parsed)
	{
		parsed = default;
		if (string.IsNullOrWhiteSpace(path)) return false;
		try
		{
			parsed = NodePath.ParseUrl(path);
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	RedirectToPageResult RedirectPostGet() =>
		RedirectToPage("/Node", new { path = Path.ToUrlPath() });

	string Actor() => User.Identity?.Name ?? "admin";
}
