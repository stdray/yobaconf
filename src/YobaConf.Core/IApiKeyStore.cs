namespace YobaConf.Core;

public interface IApiKeyStore
{
	// Returns the matching ApiKey for the given plaintext token, or null if no match.
	// Implementations compare in constant time to avoid timing leaks that would let
	// attackers discover valid tokens one character at a time. Short-circuit on length
	// mismatch is acceptable (length is not considered sensitive).
	ApiKey? Validate(string plaintextToken);
}
