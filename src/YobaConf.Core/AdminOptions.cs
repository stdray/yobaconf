namespace YobaConf.Core;

// Single-admin auth config bound from appsettings `Admin` section. Password is stored as
// a PBKDF2-encoded hash produced by `AdminPasswordHasher.Hash(plaintext)`. Never store
// plaintext here. Operator runs `dotnet YobaConf.Web.dll --hash-password <pw>` to generate
// the hash string, copies it into `Admin:PasswordHash`. Spec §14 / plan Phase A bullet
// "Bootstrap из appsettings.json — seed admin".
//
// Multi-admin with DB-backed users is Phase B+ (matches yobalog's progression).
public sealed record AdminOptions
{
	public string Username { get; init; } = string.Empty;
	public string PasswordHash { get; init; } = string.Empty;
}
