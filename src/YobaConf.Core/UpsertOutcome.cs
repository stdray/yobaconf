namespace YobaConf.Core;

// Return value of IConfigStoreAdmin.Upsert* + SoftDelete* for the Phase B optimistic-
// locking contract. Callers passing a non-null expectedHash get one of three outcomes;
// callers passing null always get Inserted or Updated (force-upsert — used by seed,
// import, rollback-restore paths where "known latest" doesn't make sense).
//
// The semantics:
//   Inserted  — row did not exist (or was soft-deleted and got revived). No hash check
//               is meaningful here; callers that pass expectedHash != null expecting an
//               existing row get Conflict instead.
//   Updated   — row existed live and (either expectedHash was null OR matched current
//               ContentHash). New value written, audit appended.
//   Conflict  — expectedHash was non-null and did not match current ContentHash (or row
//               no longer exists live). Caller should re-fetch, show merge UI, retry.
public enum UpsertOutcome
{
	Inserted,
	Updated,
	Conflict,
}
