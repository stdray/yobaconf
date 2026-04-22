using Microsoft.Extensions.Options;
using YobaConf.Core.Storage;

namespace YobaConf.Tests.Storage;

// IDisposable SQLite tmp-file fixture. Each instance owns a unique `.db` file under the
// OS temp dir; Dispose deletes it. Construct the store via `Create()` to get a properly
// wired SqliteBindingStore sharing this file.
sealed class TempDb : IDisposable
{
	public string Directory { get; }
	public string FileName { get; }
	public string Path => System.IO.Path.Combine(Directory, FileName);

	public TempDb()
	{
		Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yobaconf-tests-" + Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(Directory);
		FileName = "yobaconf.db";
	}

	public SqliteBindingStore CreateStore() =>
		new(Options.Create(new SqliteBindingStoreOptions { DataDirectory = Directory, FileName = FileName }));

	public void Dispose()
	{
		// Force GC of linq2db + SQLite connection finalizers so the file is unlocked
		// before delete. Test isolation matters more than a tight loop here.
		GC.Collect();
		GC.WaitForPendingFinalizers();
		try { System.IO.Directory.Delete(Directory, recursive: true); }
		catch { /* swallow — tmp dir cleanup is best-effort */ }
	}
}
