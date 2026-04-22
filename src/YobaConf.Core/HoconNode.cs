namespace YobaConf.Core;

// A resolved node snapshot from the store. `ContentHash` (sha256 hex of RawContent) is the
// optimistic-locking cookie: admin UI fetches it with the node and sends it back as
// `expectedHash` on save — mismatch on the round-trip means someone else wrote in between
// and the UI should show a conflict-resolution step.
public sealed record HoconNode(
	NodePath Path,
	string RawContent,
	DateTimeOffset UpdatedAt,
	string ContentHash = "");
