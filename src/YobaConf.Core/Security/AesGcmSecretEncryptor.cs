using System.Security.Cryptography;
using System.Text;

namespace YobaConf.Core.Security;

// AES-256-GCM encryption with a single master key loaded from the host environment.
// Per spec §3:
//   * Key: 32 bytes (256 bits), supplied as base64-encoded string (44 chars incl padding).
//     Generate with `openssl rand -base64 32`. In prod this is the
//     YOBACONF_MASTER_KEY env var wired from GitHub secrets; in dev it comes from
//     `dotnet user-secrets set YOBACONF_MASTER_KEY ...`.
//   * IV: 12 bytes (96 bits), fresh random per encrypt. GCM requires IV uniqueness
//     per key — reuse breaks confidentiality catastrophically. Using a CSPRNG and
//     capping at 2^32 encrypts per key keeps collision probability vanishing.
//   * AuthTag: 16 bytes (128 bits, GCM standard). Verified on Decrypt; mismatch
//     throws CryptographicException, which bubbles out of ResolvePipeline as a 500.
//   * No AAD in MVP. A future hardening could bind (Key, ScopePath) as AAD to prevent
//     swap-attacks where an attacker with DB write access moves an encrypted blob
//     from one row to another. Not in MVP threat model (single-admin, no multi-tenant).
//
// Key version "v1" is hardcoded. Rotation (spec §Security) adds a second key, decrypt
// resolves by version, writes use current. Not implemented — when it lands this class
// takes a keyring dictionary instead of a single byte[].
public sealed class AesGcmSecretEncryptor : ISecretEncryptor
{
	const string CurrentKeyVersion = "v1";
	const int IvLength = 12;
	const int AuthTagLength = 16;
	const int KeyLength = 32;

	readonly byte[] _key;

	public AesGcmSecretEncryptor(string base64MasterKey)
	{
		if (string.IsNullOrWhiteSpace(base64MasterKey))
			throw new InvalidOperationException(
				"Master key is empty. Set YOBACONF_MASTER_KEY to a base64-encoded 32-byte value " +
				"(generate locally: `openssl rand -base64 32`).");

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(base64MasterKey);
		}
		catch (FormatException ex)
		{
			throw new InvalidOperationException(
				"YOBACONF_MASTER_KEY is not valid base64. " +
				"Generate a fresh key with `openssl rand -base64 32`.", ex);
		}

		if (bytes.Length != KeyLength)
			throw new InvalidOperationException(
				$"YOBACONF_MASTER_KEY must decode to exactly {KeyLength} bytes (256 bits); " +
				$"got {bytes.Length}. Use `openssl rand -base64 32`.");

		_key = bytes;
	}

	public EncryptedSecret Encrypt(string plaintext)
	{
		ArgumentNullException.ThrowIfNull(plaintext);

		var plainBytes = Encoding.UTF8.GetBytes(plaintext);
		var iv = RandomNumberGenerator.GetBytes(IvLength);
		var ciphertext = new byte[plainBytes.Length];
		var tag = new byte[AuthTagLength];

		using var gcm = new AesGcm(_key, AuthTagLength);
		gcm.Encrypt(iv, plainBytes, ciphertext, tag);

		return new EncryptedSecret(ciphertext, iv, tag, CurrentKeyVersion);
	}

	public string Decrypt(byte[] ciphertext, byte[] iv, byte[] authTag, string keyVersion)
	{
		ArgumentNullException.ThrowIfNull(ciphertext);
		ArgumentNullException.ThrowIfNull(iv);
		ArgumentNullException.ThrowIfNull(authTag);
		ArgumentNullException.ThrowIfNull(keyVersion);

		if (!string.Equals(keyVersion, CurrentKeyVersion, StringComparison.Ordinal))
			throw new InvalidOperationException(
				$"Unknown key version '{keyVersion}'. Only '{CurrentKeyVersion}' is supported in MVP — " +
				"key rotation requires a keyring (planned follow-up).");

		var plain = new byte[ciphertext.Length];
		using var gcm = new AesGcm(_key, AuthTagLength);
		// Throws CryptographicException on tag mismatch — callers let it propagate so
		// the resolve pipeline surfaces "tampered or wrong-key" as 500.
		gcm.Decrypt(iv, ciphertext, authTag, plain);

		return Encoding.UTF8.GetString(plain);
	}
}
