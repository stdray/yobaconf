using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Extensions.Options;
using YobaConf.Core.Auth;
using YobaConf.Core.Observability;

namespace YobaConf.Core.Storage;

// SQLite-backed IUserStore + IUserAdmin. Shares the binding-store's DB file. PBKDF2 hashing
// is done by AdminPasswordHasher (Core/AdminPasswordHasher.cs) — this layer only round-trips
// the encoded string.
public sealed class SqliteUserStore : IUserStore, IUserAdmin
{
	// Precomputed PBKDF2 hash of a known-dummy password. Used on the username-miss path so
	// the login endpoint consumes ~same CPU whether the user exists or not. Lazy so
	// construction is cheap; value is immutable once computed.
	static readonly Lazy<string> DummyHash = new(() => AdminPasswordHasher.Hash("not-a-real-password"));

	readonly string dbPath;

	public SqliteUserStore(IOptions<SqliteBindingStoreOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.DataDirectory))
			throw new InvalidOperationException("Storage:DataDirectory is empty.");
		Directory.CreateDirectory(opts.DataDirectory);
		dbPath = Path.Combine(opts.DataDirectory, opts.FileName);

		using var db = Open();
		foreach (var stmt in SqliteSchema.AllStatements)
			db.Execute(stmt);
	}

	DataConnection Open()
	{
		var db = SQLiteTools.CreateDataConnection(
			$"Data Source={dbPath};Cache=Shared;Pooling=True;Foreign Keys=True");
		db.Execute("PRAGMA journal_mode=WAL;");
		return db;
	}

	public User? FindByUsername(string username)
	{
		ArgumentNullException.ThrowIfNull(username);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.find-user");
		using var db = Open();
		var row = db.GetTable<UserRow>().FirstOrDefault(r => r.Username == username);
		return row is null ? null : ToDomain(row);
	}

	public IReadOnlyList<User> ListAll()
	{
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.list-users");
		using var db = Open();
		return [.. db.GetTable<UserRow>().OrderBy(r => r.Username).ToArray().Select(ToDomain)];
	}

	public bool HasAny()
	{
		using var db = Open();
		return db.GetTable<UserRow>().Any();
	}

	public bool VerifyPassword(string username, string plaintextPassword)
	{
		ArgumentNullException.ThrowIfNull(username);
		ArgumentNullException.ThrowIfNull(plaintextPassword);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.verify-user");

		var user = FindByUsername(username);
		if (user is null)
		{
			// Dummy verify matches real-path timing so attackers can't discover valid usernames
			// via response-time side-channel.
			_ = AdminPasswordHasher.Verify(plaintextPassword, DummyHash.Value);
			return false;
		}
		return AdminPasswordHasher.Verify(plaintextPassword, user.PasswordHash);
	}

	public void Create(string username, string plaintextPassword, DateTimeOffset at)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentException.ThrowIfNullOrWhiteSpace(plaintextPassword);

		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.create-user");
		using var db = Open();
		if (db.GetTable<UserRow>().Any(r => r.Username == username))
			throw new InvalidOperationException($"User '{username}' already exists.");

		db.Insert(new UserRow
		{
			Username = username,
			PasswordHash = AdminPasswordHasher.Hash(plaintextPassword),
			CreatedAt = at.ToUnixTimeMilliseconds(),
		});
	}

	public bool UpdatePassword(string username, string plaintextPassword)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentException.ThrowIfNullOrWhiteSpace(plaintextPassword);

		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.update-user-password");
		using var db = Open();
		var affected = db.GetTable<UserRow>()
			.Where(r => r.Username == username)
			.Set(r => r.PasswordHash, AdminPasswordHasher.Hash(plaintextPassword))
			.Update();
		return affected > 0;
	}

	public bool Delete(string username)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		using var activity = ActivitySources.StorageSqlite.StartActivity("sqlite.delete-user");
		using var db = Open();
		var affected = db.GetTable<UserRow>().Where(r => r.Username == username).Delete();
		return affected > 0;
	}

	static User ToDomain(UserRow r) =>
		new(r.Username, r.PasswordHash, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt));
}
