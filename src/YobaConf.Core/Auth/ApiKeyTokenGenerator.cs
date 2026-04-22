using System.Security.Cryptography;
using System.Text;

namespace YobaConf.Core.Auth;

// 22-char url-safe base64 from 16 random bytes = 122 bits of entropy. Same format yobalog
// uses for its api-keys (ShortGuid equivalent); 22 chars fits in URLs / headers cleanly
// and beats alphanumeric-36 for rate-of-guessing resistance.
public static class ApiKeyTokenGenerator
{
	public static string New()
	{
		Span<byte> buf = stackalloc byte[16];
		RandomNumberGenerator.Fill(buf);
		// Base64Url without padding — replace + / and trim the `==` suffix to reach 22 chars.
		return Convert.ToBase64String(buf)
			.Replace('+', '-')
			.Replace('/', '_')
			.TrimEnd('=');
	}

	public static string HashHex(string plaintext)
	{
		ArgumentNullException.ThrowIfNull(plaintext);
		Span<byte> hash = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(plaintext), hash);
		return Convert.ToHexStringLower(hash);
	}

	public static string Prefix(string plaintext)
	{
		ArgumentNullException.ThrowIfNull(plaintext);
		return plaintext.Length >= 6 ? plaintext[..6] : plaintext;
	}
}
