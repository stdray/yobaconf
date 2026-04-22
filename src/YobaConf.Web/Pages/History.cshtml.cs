using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Audit;
using YobaConf.Core.Bindings;

namespace YobaConf.Web.Pages;

public sealed class HistoryModel : PageModel
{
	const int PageLimit = 200;

	readonly IAuditLogStore _audit;
	readonly IBindingStoreAdmin _bindingAdmin;
	readonly TimeProvider _clock;

	public HistoryModel(IAuditLogStore audit, IBindingStoreAdmin bindingAdmin, TimeProvider clock)
	{
		_audit = audit;
		_bindingAdmin = bindingAdmin;
		_clock = clock;
	}

	public AuditEntityType? EntityFilter { get; set; }
	public string? ActorFilter { get; set; }
	public string? KeyFilter { get; set; }

	public IReadOnlyList<KeyValuePair<string, IReadOnlyList<AuditLogEntry>>> EntriesByDay { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? SuccessMessage { get; set; }

	public void OnGet(string? entity, string? actor, string? key)
	{
		ActorFilter = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();
		KeyFilter = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
		EntityFilter = Enum.TryParse<AuditEntityType>(entity, out var e) ? e : null;
		Load();
	}

	public IActionResult OnPostRollback(long id)
	{
		// Re-fetch the entry and apply its OldValue as a new Binding upsert (Updated) or
		// re-insert (Deleted). User/ApiKey rollback is deferred — passwords/tokens can't be
		// meaningfully round-tripped from the audit payload.
		var entry = _audit.FindById(id);
		if (entry is null)
		{
			ErrorMessage = $"Audit entry {id} not found.";
			Load();
			return Page();
		}
		if (entry.EntityType != AuditEntityType.Binding)
		{
			ErrorMessage = "Rollback is only supported for Binding entries in MVP.";
			Load();
			return Page();
		}
		if (entry.Action == AuditAction.Created)
		{
			ErrorMessage = "Rolling back a Created entry is ambiguous (would mean 'delete') — use the dashboard Delete instead.";
			Load();
			return Page();
		}
		if (string.IsNullOrEmpty(entry.OldValue))
		{
			ErrorMessage = "Audit entry has no previous value to restore.";
			Load();
			return Page();
		}

		Binding candidate;
		try
		{
			candidate = BuildRestoreCandidate(entry);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Failed to parse prior value: {ex.Message}";
			Load();
			return Page();
		}

		_bindingAdmin.Upsert(candidate, $"restore:{id}");
		SuccessMessage = $"Rolled back '{entry.KeyPath}' to its state before entry #{id}.";
		Load();
		return Page();
	}

	public static bool IsRollbackEligible(AuditLogEntry e) =>
		e.EntityType == AuditEntityType.Binding
		&& e.Action is AuditAction.Updated or AuditAction.Deleted
		&& !string.IsNullOrEmpty(e.OldValue);

	void Load()
	{
		var list = _audit.Query(EntityFilter, ActorFilter, KeyFilter, PageLimit);
		EntriesByDay = [.. list
			.GroupBy(e => e.At.UtcDateTime.ToString("yyyy-MM-dd"))
			.Select(g => new KeyValuePair<string, IReadOnlyList<AuditLogEntry>>(g.Key, [.. g]))];
	}

	Binding BuildRestoreCandidate(AuditLogEntry entry)
	{
		var tagSet = TagSet.FromCanonicalJson(entry.TagSetJson!);
		var keyPath = entry.KeyPath!;
		var oldValue = entry.OldValue!;
		var now = _clock.GetUtcNow();

		if (oldValue.StartsWith("secret|", StringComparison.Ordinal))
		{
			// Format "secret|b64(ct)|b64(iv)|b64(at)|keyversion"
			var parts = oldValue.Split('|');
			if (parts.Length != 5) throw new InvalidOperationException("malformed secret payload");
			return new Binding
			{
				Id = 0,
				TagSet = tagSet,
				KeyPath = keyPath,
				Kind = BindingKind.Secret,
				Ciphertext = Convert.FromBase64String(parts[1]),
				Iv = Convert.FromBase64String(parts[2]),
				AuthTag = Convert.FromBase64String(parts[3]),
				KeyVersion = parts[4],
				ContentHash = string.Empty,
				UpdatedAt = now,
			};
		}
		return new Binding
		{
			Id = 0,
			TagSet = tagSet,
			KeyPath = keyPath,
			Kind = BindingKind.Plain,
			ValuePlain = oldValue,
			ContentHash = string.Empty,
			UpdatedAt = now,
		};
	}
}
