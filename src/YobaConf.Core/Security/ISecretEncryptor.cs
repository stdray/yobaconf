namespace YobaConf.Core.Security;

// Abstraction over the master-key cryptographic primitive so ResolvePipeline + admin UI
// don't depend on AesGcm directly (simplifies tests — fakes can produce deterministic
// "encryptions" for assertion; real impl uses YOBACONF_MASTER_KEY env var).
//
// KeyVersion is a string tag ("v1", "v2", ...) threaded through encrypt/decrypt so
// Phase-C+1 rotation can lazy-re-encrypt on read: when Decrypt sees an old keyVersion
// it uses the matching key from a keyring, and the write path on next Upsert re-encrypts
// under the current key. Not implemented in MVP — only "v1" is accepted — but the field
// is already in the Secrets schema so no migration is needed when rotation lands.
public interface ISecretEncryptor
{
	EncryptedSecret Encrypt(string plaintext);
	string Decrypt(byte[] ciphertext, byte[] iv, byte[] authTag, string keyVersion);
}

// AES-GCM output bundle. Ciphertext + IV + AuthTag together are the complete payload
// needed to decrypt; KeyVersion picks the decryption key when rotation is in use.
public sealed record EncryptedSecret(
	byte[] Ciphertext,
	byte[] Iv,
	byte[] AuthTag,
	string KeyVersion);
