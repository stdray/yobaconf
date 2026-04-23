using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Observability;
using YobaConf.Core.Tags;

namespace YobaConf.Core.Storage;

// SQLite-backed ITagVocabularyStore + ITagVocabularyAdmin. Shares the binding-store DB.
// All mutations append an AuditLog row under EntityType=TagVocabulary; the audit reuses
// the KeyPath column for the TagKey + NewValue/OldValue for the TagValue payload.
public sealed class SqliteTagVocabularyStore : ITagVocabularyStore, ITagVocabularyAdmin
{
	readonly string dbPath;

	public SqliteTagVocabularyStore(IOptions<SqliteBindingStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.DataDirectory))
			throw new InvalidOperationException(
				"Storage:DataDirectory is empty. Tag-vocabulary store shares the binding store's DB path.");
		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		using var db = Open();
		SqliteSchema.EnsureSchema(db);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection(
			$"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		return db;
	}

	public IReadOnlyList<TagVocabularyEntry> ListActive()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-tag-vocabulary");
		using var db = Open();
		var rows = db.GetTable<TagVocabularyRow>()
			.Where(r => r.IsDeleted == 0)
			.OrderBy(r => r.TagKey).ThenBy(r => r.TagValue)
			.ToArray();
		return [.. rows.Select(r => ToDomain(r))];
	}

	public IReadOnlyList<string> DistinctKeys()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.distinct-tag-keys");
		using var db = Open();
		return [.. db.GetTable<TagVocabularyRow>()
			.Where(r => r.IsDeleted == 0)
			.Select(r => r.TagKey)
			.Distinct()
			.OrderBy(k => k)
			.ToArray()];
	}

	public TagVocabularyEntry Create(string key, string? value, string? description, int priority, DateTimeOffset at, string actor = "system")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		ArgumentNullException.ThrowIfNull(actor);
		var trimmedKey = key.Trim();
		var trimmedValue = value?.Trim();
		var trimmedDescription = description?.Trim();
		if (trimmedValue is { Length: 0 }) trimmedValue = null;
		if (trimmedDescription is { Length: 0 }) trimmedDescription = null;

		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.create-tag-vocabulary");
		using var db = Open();
		using var tx = db.BeginTransaction();

		// Uniqueness at app layer too — SQLite's NULL-not-equal treats (env, null) + a
		// second (env, null) as distinct, but we want "one key-declaration row per key".
		var exists = trimmedValue is null
			? db.GetTable<TagVocabularyRow>().Any(r => r.TagKey == trimmedKey && r.TagValue == null && r.IsDeleted == 0)
			: db.GetTable<TagVocabularyRow>().Any(r => r.TagKey == trimmedKey && r.TagValue == trimmedValue && r.IsDeleted == 0);
		if (exists)
			throw new InvalidOperationException(
				trimmedValue is null
					? $"Tag key '{trimmedKey}' is already declared."
					: $"Tag pair '{trimmedKey}={trimmedValue}' is already declared.");

		var ts = at.ToUnixTimeMilliseconds();
		var id = Convert.ToInt64(db.InsertWithIdentity(new TagVocabularyRow
		{
			TagKey = trimmedKey,
			TagValue = trimmedValue,
			Description = trimmedDescription,
			Priority = priority,
			UpdatedAt = ts,
			IsDeleted = 0,
		}));

		SqliteAuditLogStore.Append(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = AuditAction.Created.ToString(),
			EntityType = AuditEntityType.TagVocabulary.ToString(),
			KeyPath = trimmedKey,
			OldValue = null,
			NewValue = FormatAudit(trimmedValue, trimmedDescription, priority),
		});
		tx.Commit();

		return new TagVocabularyEntry(id, trimmedKey, trimmedValue, trimmedDescription, priority, at);
	}

	public bool SoftDelete(long id, DateTimeOffset at, string actor = "system")
	{
		ArgumentNullException.ThrowIfNull(actor);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.soft-delete-tag-vocabulary");
		using var db = Open();
		using var tx = db.BeginTransaction();
		var existing = db.GetTable<TagVocabularyRow>().FirstOrDefault(r => r.Id == id && r.IsDeleted == 0);
		if (existing is null) return false;

		var ts = at.ToUnixTimeMilliseconds();
		db.GetTable<TagVocabularyRow>()
			.Where(r => r.Id == id && r.IsDeleted == 0)
			.Set(r => r.IsDeleted, 1)
			.Set(r => r.UpdatedAt, ts)
			.Update();

		SqliteAuditLogStore.Append(db, new AuditLogRow
		{
			At = ts,
			Actor = actor,
			Action = AuditAction.Deleted.ToString(),
			EntityType = AuditEntityType.TagVocabulary.ToString(),
			KeyPath = existing.TagKey,
			OldValue = FormatAudit(existing.TagValue, existing.Description, existing.Priority),
			NewValue = null,
		});
		tx.Commit();
		return true;
	}

	static string FormatAudit(string? value, string? description, int priority) =>
		(value, description) switch
		{
			(null, null) => $"key-only|priority={priority}",
			(null, _) => $"key-only|{description}|priority={priority}",
			(_, null) => $"value={value}|priority={priority}",
			_ => $"value={value}|{description}|priority={priority}",
		};

	static TagVocabularyEntry ToDomain(TagVocabularyRow r) =>
		new(r.Id, r.TagKey, r.TagValue, r.Description, r.Priority, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt));
}
