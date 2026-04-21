using System.Security.Cryptography;
using YobaConf.Core.Security;

namespace YobaConf.Tests.Security;

public sealed class AesGcmSecretEncryptorTests
{
	// 32 bytes of 0x42 base64-encoded. Deterministic so assertions can be exact.
	const string TestKeyBase64 = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=";

	[Fact]
	public void Roundtrip_UtfEight_PlaintextSurvives()
	{
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);
		const string plaintext = "super-secret: hostпароль!🔐";

		var bundle = enc.Encrypt(plaintext);
		var decrypted = enc.Decrypt(bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion);

		decrypted.Should().Be(plaintext);
	}

	[Fact]
	public void Encrypt_Produces_FreshIv_EachCall()
	{
		// IV uniqueness per key is the main correctness property of GCM — reuse breaks
		// confidentiality catastrophically (spec §2 "AES IV uniqueness").
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);
		const string plaintext = "same value, two encrypts";

		var a = enc.Encrypt(plaintext);
		var b = enc.Encrypt(plaintext);

		a.Iv.Should().NotEqual(b.Iv);
		a.Ciphertext.Should().NotEqual(b.Ciphertext);
		a.AuthTag.Should().NotEqual(b.AuthTag);
	}

	[Fact]
	public void Iv_Length_Is_TwelveBytes()
	{
		// GCM standard: 96-bit nonce. Our impl uses 12 bytes explicitly; deviation
		// would silently degrade security.
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);

		var bundle = enc.Encrypt("x");

		bundle.Iv.Should().HaveCount(12);
		bundle.AuthTag.Should().HaveCount(16);
		bundle.KeyVersion.Should().Be("v1");
	}

	[Fact]
	public void Decrypt_Tampered_Ciphertext_Throws_CryptographicException()
	{
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);
		var bundle = enc.Encrypt("legitimate");

		// Flip one byte of ciphertext.
		var tampered = (byte[])bundle.Ciphertext.Clone();
		tampered[0] ^= 0xFF;

		var act = () => enc.Decrypt(tampered, bundle.Iv, bundle.AuthTag, bundle.KeyVersion);
		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void Decrypt_Tampered_AuthTag_Throws_CryptographicException()
	{
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);
		var bundle = enc.Encrypt("legitimate");

		var tampered = (byte[])bundle.AuthTag.Clone();
		tampered[0] ^= 0xFF;

		var act = () => enc.Decrypt(bundle.Ciphertext, bundle.Iv, tampered, bundle.KeyVersion);
		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void Decrypt_With_WrongKey_Throws_CryptographicException()
	{
		var encA = new AesGcmSecretEncryptor(TestKeyBase64);
		var encB = new AesGcmSecretEncryptor("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="); // 32 bytes 0x00..0x1F

		var bundle = encA.Encrypt("secret");

		var act = () => encB.Decrypt(bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion);
		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void Ctor_MissingKey_Throws_WithGuidance()
	{
		var act = () => new AesGcmSecretEncryptor(string.Empty);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*YOBACONF_MASTER_KEY*openssl rand -base64 32*");
	}

	[Fact]
	public void Ctor_InvalidBase64_Throws_WithGuidance()
	{
		var act = () => new AesGcmSecretEncryptor("not-base64-!!!");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*not valid base64*")
			.WithInnerException<FormatException>();
	}

	[Fact]
	public void Ctor_WrongKeyLength_Throws_WithGuidance()
	{
		// 16-byte key base64 (AES-128) — we require 32 bytes.
		var act = () => new AesGcmSecretEncryptor("AAECAwQFBgcICQoLDA0ODw==");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*32 bytes*");
	}

	[Fact]
	public void Decrypt_UnknownKeyVersion_Throws()
	{
		var enc = new AesGcmSecretEncryptor(TestKeyBase64);
		var bundle = enc.Encrypt("x");

		var act = () => enc.Decrypt(bundle.Ciphertext, bundle.Iv, bundle.AuthTag, "v99");
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown key version 'v99'*");
	}
}
