namespace YobaConf.Core;

// Plaintext variable scoped to a NodePath. Visible to all descendants of `ScopePath`
// per spec §1 / §4.4: variables inherit down the tree, nearest scope wins on Key
// collision with a farther scope. `ContentHash` (sha256 hex of Value) is the
// optimistic-locking cookie for inline-edit in the admin UI (Phase B).
public sealed record Variable(
	string Key,
	string Value,
	NodePath ScopePath,
	DateTimeOffset UpdatedAt,
	bool IsDeleted = false,
	string ContentHash = "");
