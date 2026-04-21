namespace YobaConf.Core;

// Plaintext variable scoped to a NodePath. Visible to all descendants of `ScopePath`
// per spec §1 / §4.4: variables inherit down the tree, nearest scope wins on Key
// collision with a farther scope.
public sealed record Variable(
	string Key,
	string Value,
	NodePath ScopePath,
	DateTimeOffset UpdatedAt,
	bool IsDeleted = false);
