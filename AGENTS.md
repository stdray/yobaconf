# AGENTS.md

Guidance for coding agents working in this repository.

## Status: Phase A.0 bootstrap complete, no domain code yet

Toolchain, build pipeline, CI, and scaffolded Core / Web / Tests projects are in place. `doc/plan.md` Phase A.0 and A.1 are closed; Phase A (domain code — `SqliteConfigStore`, include preprocessor, resolve pipeline) is next.

## Build entry points

- **Local:** `./build.sh --target=Test` (bash) or `pwsh ./build.ps1 -Target Test` (PowerShell). Bootstrap restores Cake + GitVersion tools, then runs the Cake task.
- **Cake tasks:** Clean → Restore → Version (GitVersion) → Build → Test → Docker → DockerSmoke → DockerPush. Plus a standalone `Dev` task for the watcher loop.
- **Dev loop:** `./build.sh --target=Dev` / `pwsh ./build.ps1 -Target Dev` runs `bun run dev` (ts + css watchers via concurrently) and `dotnet watch` in parallel, both streaming to the current terminal. Ctrl+C kills both trees. Replaces the older two-window `run_dev.ps1`.
- **Other local targets:** `--target=Docker` builds the image locally; `--target=DockerPush --dockerPush=true` requires `GHCR_USERNAME` + `GHCR_TOKEN` env or prior `docker login`.
- **CI:** mirrors `./build.sh --target=Test` for the fast lane; on `main` push + `deploy` tag push also runs `--target=DockerPush` with ghcr.io credentials.
- **Deploy:** **manual tag `deploy` only**. `git tag deploy && git push origin deploy --force` (force needed because the tag gets re-used). Main-push publishes the image but does NOT SSH-deploy — prevents accidental prod updates on every merge.

## Documents — what goes where

The design is split across three files; keep them that way:

- **`doc/spec.md`** — the specification. §1–§12 cover domain principles, stack, data model, resolve pipeline, UI, scalability, audit & versioning, API design, frontend, build, self-observability, and localization. No progress, no checkboxes, no task tracking.
- **`doc/plan.md`** — phases A.0, A.1, A–E, pre-Phase-A test coverage, hard invariants, and open questions. This is where checkboxes and progress live.
- **`doc/decision-log.md`** — every architectural decision with date / decision / reason / what was rolled back. **Newest entries go on top.** When you make or propose an architectural change, add an entry here; don't bury the reasoning in a commit message.

When editing: spec changes go to `spec.md`, progress updates to `plan.md`, and any decision that changes direction gets a new `decision-log.md` entry.

## Target stack (planned, not yet scaffolded)

- .NET 10 monolith, Razor Pages SSR + htmx (+ jQuery, optional Alpine.js).
- SQLite via `linq2db.SQLite.MS` as the single-file row store for `Nodes`, `Variables`, `Secrets`, `ApiKeys`, `AuditLog`. Same stack as yobalog by design — see `decision-log.md` 2026-04-21 "SQLite + linq2db вместо LiteDB".
- HOCON as the edit format; JSON as the delivery format. Parser = `Hocon` 2.0.4 + `Hocon.Configuration` 2.0.4 (akkadotnet/HOCON). Phase A.1 gate closed 2026-04-21. **Substitution resolves at parse-time**, not after `.WithFallback` — see pipeline §4 in spec.
- AES-256 for secret values at rest. Master key — environment variable (`YOBACONF_MASTER_KEY`), never in `appsettings.json` or in the DB.
- HOCON viewing (Phase A): Prism.js with a hand-ported HOCON component (from sabieber/vscode-hocon TextMate grammar, ~80 lines of regex). Read-only highlighting for the tree + JSON preview, ~25 KB bundle.
- HOCON editing (Phase B): CodeMirror 6 with a StreamLanguage HOCON tokenizer (same TextMate grammar ported to ~150 lines). Diff view via `@codemirror/merge`. Picked over Monaco Editor — 10× smaller bundle, simpler ESM integration, matches Obsidian's editor so the ConflictSolverService three-way-merge pattern carries over. See `decision-log.md` 2026-04-21 "CodeMirror 6 + Prism вместо Monaco".
- Frontend build: TypeScript + Tailwind via `bun` (not npm+node). `package.json` lives next to `.csproj`; Release builds invoke `bun run build` from an MSBuild target.

## Hard invariants (easy to violate — read before coding)

- **Resolve pipeline is deterministic.** Same input (`path` + API key + DB snapshot) → exactly one JSON output. Enforced by snapshot tests: fixture (nodes + HOCON + variables + secrets) + query → expected JSON. Any divergence = regression in `spec.md` §4 pipeline.
- **No YobaConf self-config.** YobaConf configures itself from `appsettings.json` only. Do not read bootstrap settings from a YobaConf node (`$system/yobaconf` or otherwise) — it creates a bootstrap cycle. Master AES key is an environment variable.
- **No YobaLog → YobaConf dependency.** Events flow YobaConf → YobaLog via CLEF only. YobaLog's own config is `appsettings.json` (fixed in its own spec), so the reverse dependency is impossible by construction — don't accidentally add it.
- **Secrets in the audit log are always encrypted.** `spec.md` §7. Utility test: grep the `.db` file after writing a secret; plaintext must not be there.
- **Variables and Secrets live in separate tables.** Not one table with an `IsSecret` flag. The split is type-safety — a forgotten `if (row.IsSecret) decrypt(...)` branch in a unified table would leak plaintext. See `decision-log.md` 2026-04-21.
- **No workspace concept.** Hierarchical paths ARE the namespace. Isolation between tenants/projects = scoped API keys on `RootPath`. If you feel the urge to add `/v1/conf/{workspace}/{path}` for "symmetry with yobalog" — don't. See `decision-log.md` 2026-04-21 "Без workspaces в MVP".
- **Explicit `include`, no auto ancestor-merge.** Node content is NOT automatically merged with ancestors. Author pulls in what they want via `include "absolute-target-path"` in `RawContent`. Validation: `dir(target)` must be ancestor-or-equal `dir(including-node)` — permits ancestors and siblings-in-same-dir, rejects descendants, sibling-subtrees, self-include. Only absolute paths (no relative `../`). Fallthrough remains for routing only (missing `/a/b/c` → serve best-match ancestor's content), not merge. See `decision-log.md` 2026-04-21 "Include-семантика финализирована".
- **Include resolution is a preprocess step, not the HOCON `ConfigResolver` callback.** Own DFS with `HashSet<NodePath> visited` for cycle detection — sibling-includes can form cycles (service1 ↔ service2). The flattened include-free HOCON text is then handed to `HoconConfigurationFactory.ParseString`. Native `ConfigResolver` callback lacks "who is including" context, so scope validation and cycle tracking can't be expressed on it cleanly.
- **403 before 404.** Authorization check runs before node lookup. A key without access to `path` gets `403` whether or not the node exists — no existence leak.
- **Node-name slug regex `^[a-z0-9][a-z0-9-]{1,39}$`.** Dot is the path separator in URLs — forbidden inside a segment. `$`-prefix reserved for system nodes (`$system`, `$bootstrap`). Matches YobaLog's workspace-id regex exactly.
- **API-key scope = segment-wise prefix, not substring.** A key scoped to `yobaproj.yobaapp` must let through `yobaproj.yobaapp.dev` and must reject `yobaproj.yobaapplication`. Compare after splitting on `.`, never with `StartsWith` on the raw string.
- **Localization from day one.** All user-facing strings go through `IStringLocalizer`. No hardcoded strings in Razor/code. While the i18n scaffold isn't built, **all user-facing strings are literal English ASCII** — the CI will have a non-ASCII check over `ts/` and `Pages/` that fails the build on Cyrillic or other non-ASCII chars in those files. Comments in `.cs` under `src/YobaConf.Core` and `tests/` are exempt.
- **UI test selectors: `data-testid` required, text-matching forbidden on chrome.** Display strings are localization targets — a test that matches them breaks on the first translation. Same for CSS classes (styling concern, subject to DaisyUI/Tailwind refactors) and accessible-name roles (`GetByRole(Name = "Apply")` also reads the localized label). Rules (Playwright .NET — `tests/YobaConf.E2ETests/` will appear in Phase B):
    - Every element a test interacts with or asserts on gets a stable `data-testid="<kebab-slug>"` in the Razor markup. Slugs are English, domain-specific, stable across locales (`node-tree-item`, `hocon-editor`, `secret-reveal`).
    - Locate via `page.GetByTestId(…)`. **Forbidden** for chrome: `GetByText`, `GetByRole(Name = …)`, `GetByPlaceholder`, CSS class selectors (`.btn-primary`, `.alert-error`) — all re-couple tests to display strings or styling.
    - `HasText = …` allowed **only** inside a testid-scoped locator, and **only** for asserting user-generated data content (node paths, variable names, API-key descriptions) — never UI chrome.
    - ARIA roles without a name filter (`GetByRole(AriaRole.Tree)`) acceptable when unambiguous on the page, but testid preferred.
- **No HTML / UI templates in `.cs` files.** Razor (`.cshtml` partials + `IRazorPartialRenderer`) owns all markup. Building HTML in a `StringBuilder` from a `.cs` service makes classes invisible to Tailwind's JIT scan (purge drops them silently). If an endpoint needs to return HTML, route it through a Razor partial.
- **Frontend build is Release-only.** Debug doesn't invoke `bun` from MSBuild — use a manual `bun run dev` (or a `run_dev.ps1` mirror of yobalog) for the watcher loop. This keeps `dotnet watch` and `bun --watch` from racing on `wwwroot/` output.

## Coding style

- **Immutability and functional approach by default**, both backend and frontend. In C#: `record`/`readonly record struct` over classes, `init`-only properties, `ImmutableArray<T>`/`IReadOnlyList<T>` over `List<T>` in APIs, pure functions over stateful services where practical, `switch` expressions over mutation. In TypeScript: `const` everywhere, `readonly` on types, `ReadonlyArray<T>`, spread/map/filter over `push`/splice. Mutation is allowed only where it's load-bearing (hot paths, Monaco model edits) — and must be local, not leaked through APIs.
- **Arrow/expression-bodied style when it fits.** C#: expression-bodied members (`=>`) for methods/properties/ctors that are a single expression (`public int Count => items.Length;`, `public static Foo Parse(string s) => TryParse(s, out var f) ? f : throw ...;`); use `switch` expressions over `switch` statements. TypeScript: arrow functions for callbacks and module-level helpers (`const add = (a: number, b: number) => a + b;`); reserve `function` for cases that need hoisting or `this` binding. Don't force arrow style when the body legitimately has multiple statements — readability wins over uniformity.
- **Omit implicit access modifiers.** Don't write the language default: `internal` on top-level types, `private` on class/struct members and constructors, `public` on interface members. `class Foo` instead of `internal class Foo`; `string _name;` instead of `private string _name;`; constructors in a `public` record struct stay unmarked when private is intended. Always write `public`/`protected`/`internal` when they differ from the default. Same principle for TypeScript: don't write the default (`public` on class members is implicit — omit it).
- **Maximum static typing — no escape hatches.** C#: no `object`, no `dynamic`; use generics, discriminated-union-style `record` hierarchies, or `OneOf<>`-style types. The only place `JsonElement`/`JsonNode` is acceptable is the delivery boundary (final JSON response) — and it must be parsed into typed shapes before flowing back into the resolve pipeline. TypeScript: `strict: true`, no `any`, no `unknown` in public APIs (use it only at runtime-validation boundaries — e.g. parsing network responses — and narrow immediately via a type guard or schema). Prefer discriminated unions and branded types over string primitives for IDs (`NodePath`, `ApiKeyId`, `WorkspaceId`).
- **Formatting:** indent with **tabs**, rendered as **2 spaces wide**. Enforced via `.editorconfig` at the repo root — don't override per-editor. Final newline, UTF-8, trimmed trailing whitespace. LF line endings in source files (`.gitattributes` normalizes).

## Commit convention (synchronised with yobalog)

Both repos follow **Conventional Commits** for consistency.

- **Subject:** `type(scope): short description`, ≤ 72 chars, imperative mood, no trailing period.
    - Types in use: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`.
    - Scopes (indicative, add as needed): `core`, `web`, `hocon`, `include`, `schema`, `admin`, `e2e`, `css`, `bootstrap`, `docs`, `deps`.
- **Body (markdown):**
    - Bolded section headers (`**Thing.**`) separate distinct changes when a commit touches multiple concerns.
    - Explain the **why** and the tricky bits — don't narrate what the diff already shows.
    - End with a test-totals footer when tests were run: `Totals: N unit + M E2E = X green, R/R stable runs.` (R/R is how many times the full suite passed out of how many attempted — skip if only one run).
    - `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` footer when the commit was AI-assisted.
- **ASCII-only in commit bodies.** Matches spec §12 rules on user-facing strings; commit metadata is subject to the same constraint so `git log` reads cleanly in any terminal.

## Working style for this repo

- When the user asks about "next step" or "what to do," check `doc/plan.md` — phases are the source of truth for ordering.
- Russian is fine in conversation and in decision-log entries; user-facing code strings are English ASCII (see plan invariants).
- This is a greenfield project — don't add backwards-compatibility shims, feature flags, or `// TODO: remove` comments. The spec and decision log are how we remember why things exist.
- **Plan update goes in the same commit as the feature.** When you complete or meaningfully shift a phase / bullet from `doc/plan.md`, update the file in the same commit.
- YobaConf is a sibling of YobaLog (`D:\my\prj\yobalog`). When making tooling/style/infra decisions, check there first — if it's solved, copy; if yobaconf needs to diverge, add a `decision-log.md` entry explaining why.
