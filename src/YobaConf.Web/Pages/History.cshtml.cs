using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;
using YobaConf.Core.Storage;

namespace YobaConf.Web.Pages;

// Phase B.6 — unified audit-log timeline + rollback.
//
// URL: /History?path=X&scope=exact|desc|all&types=Node,Variable,Secret&page=0
//
// Rollback semantics (single-entry): pick one AuditEntry.Id; the server reconstructs the
// pre-change state and issues a fresh Upsert* with `actor = "restore:<id>"`. This creates
// a NEW audit row so the timeline remains append-only and the action is itself auditable.
// Origin entries (`Action == Created`) are explicitly not rollbackable — call Delete
// instead (separate flow). Divergent-state preview / 3-way merge for rollback is a
// documented follow-up; in Phase B.6 we just apply the reverse without a diff.
[IgnoreAntiforgeryToken]
public sealed class HistoryModel : PageModel
{
	const int PageSize = 50;

	readonly IAuditLogStore _audit;
	readonly IConfigStoreAdmin _admin;
	readonly TimeProvider _clock;

	public HistoryModel(IAuditLogStore audit, IConfigStoreAdmin admin, TimeProvider clock)
	{
		_audit = audit;
		_admin = admin;
		_clock = clock;
	}

	public NodePath Path { get; private set; }
	public ScopeFilter Scope { get; private set; } = ScopeFilter.Exact;
	public HashSet<AuditEntityType> Types { get; private set; } = [AuditEntityType.Node, AuditEntityType.Variable, AuditEntityType.Secret, AuditEntityType.ApiKey];
	public int PageIndex { get; private set; }
	public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
	public string? ErrorMessage { get; private set; }
	public string? SuccessMessage { get; private set; }

	public IActionResult OnGet(string? path, string? scope, string? types, int page = 0)
	{
		if (!string.IsNullOrWhiteSpace(path))
		{
			try { Path = NodePath.ParseUrl(path); }
			catch (ArgumentException) { return BadRequest(); }
		}
		Scope = ParseScope(scope);
		if (!string.IsNullOrWhiteSpace(types))
			Types = [.. types.Split(',').Select(ParseType).OfType<AuditEntityType>()];
		PageIndex = Math.Max(0, page);

		Load();

		if (TempData["Error"] is string err) ErrorMessage = err;
		if (TempData["Success"] is string ok) SuccessMessage = ok;

		return Page();
	}

	public IActionResult OnPostRollback(long id, string? path, string? scope, string? types, int page = 0)
	{
		var entry = _audit.FindById(id);
		if (entry is null)
		{
			TempData["Error"] = $"Audit entry #{id} not found.";
			return RedirectWithQuery(path, scope, types, page);
		}

		if (entry.Action == AuditAction.Created)
		{
			TempData["Error"] = "Create entries cannot be rolled back (delete the entity instead).";
			return RedirectWithQuery(path, scope, types, page);
		}

		var actor = $"restore:{id}";
		UpsertOutcome outcome;
		switch (entry.EntityType)
		{
			case AuditEntityType.Node:
				outcome = RollbackNode(entry, actor);
				break;
			case AuditEntityType.Variable:
				outcome = RollbackVariable(entry, actor);
				break;
			case AuditEntityType.Secret:
				outcome = RollbackSecret(entry, actor);
				break;
			default:
				TempData["Error"] = $"Rollback for {entry.EntityType} not implemented in Phase B.6.";
				return RedirectWithQuery(path, scope, types, page);
		}

		TempData[outcome == UpsertOutcome.Conflict ? "Error" : "Success"] = outcome == UpsertOutcome.Conflict
			? $"Rollback of entry #{id} failed (divergent state); retry after reload."
			: $"Rolled back entry #{id} ({entry.EntityType} {entry.Action}).";

		return RedirectWithQuery(path, scope, types, page);
	}

	UpsertOutcome RollbackNode(AuditEntry entry, string actor) =>
		entry.Action switch
		{
			// Updated N → restore OldValue (RawContent before the change).
			AuditAction.Updated when entry.OldValue is not null =>
				_admin.UpsertNode(entry.Path, entry.OldValue, _clock.GetUtcNow(), actor),
			// Deleted N → revive with pre-delete content.
			AuditAction.Deleted when entry.OldValue is not null =>
				_admin.UpsertNode(entry.Path, entry.OldValue, _clock.GetUtcNow(), actor),
			// Restored N treated same as Updated.
			AuditAction.Restored when entry.OldValue is not null =>
				_admin.UpsertNode(entry.Path, entry.OldValue, _clock.GetUtcNow(), actor),
			_ => UpsertOutcome.Conflict,
		};

	UpsertOutcome RollbackVariable(AuditEntry entry, string actor)
	{
		if (entry.Key is null) return UpsertOutcome.Conflict;
		return entry.Action switch
		{
			AuditAction.Updated when entry.OldValue is not null =>
				_admin.UpsertVariable(entry.Path, entry.Key, entry.OldValue, _clock.GetUtcNow(), actor),
			AuditAction.Deleted when entry.OldValue is not null =>
				_admin.UpsertVariable(entry.Path, entry.Key, entry.OldValue, _clock.GetUtcNow(), actor),
			AuditAction.Restored when entry.OldValue is not null =>
				_admin.UpsertVariable(entry.Path, entry.Key, entry.OldValue, _clock.GetUtcNow(), actor),
			_ => UpsertOutcome.Conflict,
		};
	}

	UpsertOutcome RollbackSecret(AuditEntry entry, string actor)
	{
		if (entry.Key is null || entry.OldValue is null) return UpsertOutcome.Conflict;
		if (!SqliteConfigStore.TryDeserializeSecretBundle(entry.OldValue, out var ct, out var iv, out var tag, out var kv))
			return UpsertOutcome.Conflict;
		return entry.Action switch
		{
			AuditAction.Updated =>
				_admin.UpsertSecret(entry.Path, entry.Key, ct, iv, tag, kv, _clock.GetUtcNow(), actor),
			AuditAction.Deleted =>
				_admin.UpsertSecret(entry.Path, entry.Key, ct, iv, tag, kv, _clock.GetUtcNow(), actor),
			AuditAction.Restored =>
				_admin.UpsertSecret(entry.Path, entry.Key, ct, iv, tag, kv, _clock.GetUtcNow(), actor),
			_ => UpsertOutcome.Conflict,
		};
	}

	void Load()
	{
		var includeDescendants = Scope != ScopeFilter.Exact;
		// ScopeFilter.All = start from root + include descendants.
		var queryPath = Scope == ScopeFilter.All ? NodePath.Root : Path;
		var raw = _audit.FindByPath(queryPath, includeDescendants, PageIndex * PageSize, PageSize);
		Entries = [.. raw.Where(e => Types.Contains(e.EntityType))];
	}

	RedirectToPageResult RedirectWithQuery(string? path, string? scope, string? types, int page) =>
		RedirectToPage("/History", new { path, scope, types, page });

	static ScopeFilter ParseScope(string? s) => s switch
	{
		"desc" => ScopeFilter.Descendants,
		"all" => ScopeFilter.All,
		_ => ScopeFilter.Exact,
	};

	static AuditEntityType? ParseType(string s) => Enum.TryParse<AuditEntityType>(s, ignoreCase: true, out var t) ? t : null;
}

public enum ScopeFilter
{
	Exact,
	Descendants,
	All,
}
