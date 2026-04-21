namespace YobaConf.Core;

// Output of ResolvePipeline. `Json` is canonical (ordinal-sorted object keys for
// determinism, per HoconJsonSerializer). `ETag` is first 16 hex chars of sha256(JSON bytes)
// — spec §4.6. Strong ETag (no W/ prefix) because byte-equality holds across equivalent
// inputs thanks to canonical serialisation.
public sealed record ResolveResult(string Json, string ETag);
