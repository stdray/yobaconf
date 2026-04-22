using System.Security.Cryptography;
using System.Text;
using YobaConf.Core;

namespace YobaConf.Tests.Fakes;

// In-memory IConfigStore + IConfigStoreAdmin + IAuditLogStore double for unit and
// integration tests. Mutable collections so admin-UI tests can seed + mutate from the test
// body. Hash + audit semantics match SqliteConfigStore so tests can assert on the same
// UpsertOutcome contract.
public sealed class InMemoryConfigStore : IConfigStore, IConfigStoreAdmin, IAuditLogStore
{
	readonly Dictionary<NodePath, HoconNode> nodes;
	readonly Dictionary<NodePath, List<Variable>> variables;
	readonly Dictionary<NodePath, List<Secret>> secrets;
	readonly List<AuditEntry> audit = [];
	long nextAuditId = 1;

	public InMemoryConfigStore(
		IReadOnlyDictionary<NodePath, HoconNode>? nodes = null,
		IEnumerable<Variable>? variables = null,
		IEnumerable<Secret>? secrets = null)
	{
		this.nodes = nodes is null
			? []
			: new Dictionary<NodePath, HoconNode>(nodes);
		this.variables = (variables ?? []).GroupBy(v => v.ScopePath)
			.ToDictionary(g => g.Key, g => g.ToList());
		this.secrets = (secrets ?? []).GroupBy(s => s.ScopePath)
			.ToDictionary(g => g.Key, g => g.ToList());
	}

	public HoconNode? FindNode(NodePath path) =>
		nodes.TryGetValue(path, out var node) ? node : null;

	public IReadOnlyList<NodePath> ListNodePaths() =>
		[.. nodes.Keys.OrderBy(p => p.ToDbPath(), StringComparer.Ordinal)];

	public IReadOnlyList<Variable> FindVariables(NodePath scope) =>
		variables.TryGetValue(scope, out var list) ? [.. list] : [];

	public IReadOnlyList<Secret> FindSecrets(NodePath scope) =>
		secrets.TryGetValue(scope, out var list) ? [.. list] : [];

	public UpsertOutcome UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		var hash = Sha256Hex(rawContent);
		var existing = nodes.TryGetValue(path, out var node) ? node : null;
		if (existing is null)
		{
			if (expectedHash is not null) return UpsertOutcome.Conflict;
			nodes[path] = new HoconNode(path, rawContent, updatedAt, hash);
			AppendAudit(AuditAction.Created, AuditEntityType.Node, path, null, null, rawContent, null, hash, actor, updatedAt);
			return UpsertOutcome.Inserted;
		}
		if (expectedHash is not null && existing.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		if (existing.ContentHash == hash) return UpsertOutcome.Updated;
		nodes[path] = new HoconNode(path, rawContent, updatedAt, hash);
		AppendAudit(AuditAction.Updated, AuditEntityType.Node, path, null, existing.RawContent, rawContent, existing.ContentHash, hash, actor, updatedAt);
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		var hash = Sha256Hex(value);
		var list = variables.TryGetValue(scope, out var existing) ? existing : (variables[scope] = []);
		var current = list.FirstOrDefault(v => v.Key == key && !v.IsDeleted);
		if (current is null)
		{
			if (expectedHash is not null) return UpsertOutcome.Conflict;
			list.Add(new Variable(key, value, scope, updatedAt, false, hash));
			AppendAudit(AuditAction.Created, AuditEntityType.Variable, scope, key, null, value, null, hash, actor, updatedAt);
			return UpsertOutcome.Inserted;
		}
		if (expectedHash is not null && current.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		if (current.ContentHash == hash) return UpsertOutcome.Updated;
		list.Remove(current);
		list.Add(new Variable(key, value, scope, updatedAt, false, hash));
		AppendAudit(AuditAction.Updated, AuditEntityType.Variable, scope, key, current.Value, value, current.ContentHash, hash, actor, updatedAt);
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null)
	{
		var hash = Sha256HexOfBytes(encryptedValue);
		var list = secrets.TryGetValue(scope, out var existing) ? existing : (secrets[scope] = []);
		var current = list.FirstOrDefault(s => s.Key == key && !s.IsDeleted);
		if (current is null)
		{
			if (expectedHash is not null) return UpsertOutcome.Conflict;
			list.Add(new Secret(key, encryptedValue, iv, authTag, keyVersion, scope, updatedAt, false, hash));
			AppendAudit(AuditAction.Created, AuditEntityType.Secret, scope, key, null, "bundle", null, hash, actor, updatedAt);
			return UpsertOutcome.Inserted;
		}
		if (expectedHash is not null && current.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		if (current.ContentHash == hash) return UpsertOutcome.Updated;
		list.Remove(current);
		list.Add(new Secret(key, encryptedValue, iv, authTag, keyVersion, scope, updatedAt, false, hash));
		AppendAudit(AuditAction.Updated, AuditEntityType.Secret, scope, key, "bundle", "bundle", current.ContentHash, hash, actor, updatedAt);
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteNode(NodePath path, string actor = "system", string? expectedHash = null)
	{
		if (!nodes.TryGetValue(path, out var existing)) return UpsertOutcome.Conflict;
		if (expectedHash is not null && existing.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		nodes.Remove(path);
		AppendAudit(AuditAction.Deleted, AuditEntityType.Node, path, null, existing.RawContent, null, existing.ContentHash, null, actor, DateTimeOffset.UtcNow);
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteVariable(NodePath scope, string key, string actor = "system", string? expectedHash = null)
	{
		if (!variables.TryGetValue(scope, out var list)) return UpsertOutcome.Conflict;
		var current = list.FirstOrDefault(v => v.Key == key && !v.IsDeleted);
		if (current is null) return UpsertOutcome.Conflict;
		if (expectedHash is not null && current.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		list.Remove(current);
		AppendAudit(AuditAction.Deleted, AuditEntityType.Variable, scope, key, current.Value, null, current.ContentHash, null, actor, DateTimeOffset.UtcNow);
		return UpsertOutcome.Updated;
	}

	public UpsertOutcome SoftDeleteSecret(NodePath scope, string key, string actor = "system", string? expectedHash = null)
	{
		if (!secrets.TryGetValue(scope, out var list)) return UpsertOutcome.Conflict;
		var current = list.FirstOrDefault(s => s.Key == key && !s.IsDeleted);
		if (current is null) return UpsertOutcome.Conflict;
		if (expectedHash is not null && current.ContentHash != expectedHash) return UpsertOutcome.Conflict;
		list.Remove(current);
		AppendAudit(AuditAction.Deleted, AuditEntityType.Secret, scope, key, "bundle", null, current.ContentHash, null, actor, DateTimeOffset.UtcNow);
		return UpsertOutcome.Updated;
	}

	public IReadOnlyList<AuditEntry> FindByPath(NodePath path, bool includeDescendants, int skip, int take)
	{
		IEnumerable<AuditEntry> q = audit;
		if (includeDescendants)
			q = q.Where(e => e.Path.Equals(path) || path.IsAncestorOf(e.Path));
		else
			q = q.Where(e => e.Path.Equals(path));
		return [.. q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(skip).Take(take)];
	}

	public AuditEntry? FindById(long id) => audit.FirstOrDefault(e => e.Id == id);

	void AppendAudit(AuditAction action, AuditEntityType type, NodePath path, string? key, string? oldV, string? newV, string? oldH, string? newH, string actor, DateTimeOffset at)
	{
		audit.Add(new AuditEntry(nextAuditId++, at, actor, action, type, path, key, oldV, newV, oldH, newH));
	}

	static string Sha256Hex(string s) => Sha256HexOfBytes(Encoding.UTF8.GetBytes(s));

#pragma warning disable CA1308
	static string Sha256HexOfBytes(byte[] bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308

	// Shortcut helpers for existing tests that don't care about the actor/hash ceremony.
	// New Phase-B tests use the full IConfigStoreAdmin signature directly.
	public void SeedNode(NodePath path, string rawContent, DateTimeOffset? at = null) =>
		UpsertNode(path, rawContent, at ?? DateTimeOffset.UnixEpoch, "seed");
	public void SeedVariable(NodePath scope, string key, string value, DateTimeOffset? at = null) =>
		UpsertVariable(scope, key, value, at ?? DateTimeOffset.UnixEpoch, "seed");
	public void SeedSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset? at = null) =>
		UpsertSecret(scope, key, encryptedValue, iv, authTag, keyVersion, at ?? DateTimeOffset.UnixEpoch, "seed");

	public static InMemoryConfigStore With(params (string dbPath, string hocon)[] entries)
	{
		var store = new InMemoryConfigStore();
		foreach (var (dbPath, hocon) in entries)
			store.SeedNode(NodePath.ParseDb(dbPath), hocon);
		return store;
	}
}
