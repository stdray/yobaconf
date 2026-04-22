namespace YobaConf.Core;

// Encrypted secret scoped to a NodePath. Same visibility rules as Variable (inherits to
// descendants, nearest scope wins). Decryption is a separate stage in the resolve pipeline
// (§4.4): VariableScopeResolver only identifies which secrets are in-scope; an
// ISecretDecryptor (Phase C wiring) takes the encrypted payload + mastering key from env
// and produces plaintext before HOCON rendering.
//
// AES-256-GCM fields follow spec §3: fresh IV per encrypt (12 bytes), GCM AuthTag (16 bytes),
// KeyVersion for master-key rotation. byte[] equality here is reference-based which is fine —
// we compare secrets by (Key, ScopePath) identity, not by content.
//
// `ContentHash` (sha256 hex of ciphertext bytes) is the optimistic-locking cookie for
// admin UI edit/delete (Phase B).
public sealed record Secret(
	string Key,
	byte[] EncryptedValue,
	byte[] Iv,
	byte[] AuthTag,
	string KeyVersion,
	NodePath ScopePath,
	DateTimeOffset UpdatedAt,
	bool IsDeleted = false,
	string ContentHash = "");
