namespace YobaConf.Core.Storage;

public sealed record SqliteConfigStoreOptions
{
	// Directory that holds the SQLite file. Relative paths resolved against the process
	// working directory at startup. In the prod container, operators mount a host volume
	// here (spec §11): `docker run ... -v /opt/yobaconf/data:/app/data ...`.
	public string DataDirectory { get; init; } = "./data";

	// Single DB file name. One-file store for the whole config tree (spec §2 — yobaconf
	// isn't workspace-split the way yobalog is, so there's no per-workspace file).
	public string FileName { get; init; } = "yobaconf.db";
}
