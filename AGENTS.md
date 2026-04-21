# AGENTS.md

Guidance for coding agents working in this repository.

## Status: pre-code, spec-stage

There is no code yet — only design docs under `doc/`. No `.csproj`, no `package.json`, no build/test/lint commands. The first implementation step is the Phase A.0 Bootstrap (tooling skeleton) followed by the Phase A.1 HOCON gate — both described in `doc/plan.md`.

## Documents — what goes where

The design is split across three files; keep them that way:

- **`doc/spec.md`** — the specification. §1–§12 cover domain principles, stack, data model, resolve pipeline, UI, scalability, audit & versioning, API design, frontend, build, self-observability, and localization. No progress, no checkboxes, no task tracking.
- **`doc/plan.md`** — phases A.0, A.1, A–E, pre-Phase-A test coverage, hard invariants, and open questions. This is where checkboxes and progress live.
- **`doc/decision-log.md`** — every architectural decision with date / decision / reason / what was rolled back. **Newest entries go on top.** When you make or propose an architectural change, add an entry here; don't bury the reasoning in a commit message.

When editing: spec changes go to `spec.md`, progress updates to `plan.md`, and any decision that changes direction gets a new `decision-log.md` entry.

## Target stack (planned, not yet scaffolded)

- .NET 10 monolith, Razor Pages SSR + htmx (+ jQuery, optional Alpine.js).
- LiteDB as the single-file NoSQL store for `Nodes`, `Variables`, `ApiKeys`, `AuditLog`.
- HOCON as the edit format; JSON as the delivery format. Parser chosen in Phase A.1 gate (Hocon.Net / Akka.NET hocon-cs / alternative). Must support `include`, `.WithFallback()`, programmatic variable injection, `.Resolve()` — whichever package fails any of those falls out at the gate.
- AES-256 for secret values at rest. Master key — environment variable (`YOBACONF_MASTER_KEY`), never in `appsettings.json` or in the DB.
- Monaco Editor (loaded as an npm package) with a custom TextMate grammar for HOCON syntax highlighting.
- Frontend build: TypeScript + Tailwind + Monaco via `bun` (not npm+node). `package.json` lives next to `.csproj`; Release builds invoke `bun run build` from an MSBuild target.

## Hard invariants (easy to violate — read before coding)

- **Resolve pipeline is deterministic.** Same input (`path` + API key + DB snapshot) → exactly one JSON output. Enforced by snapshot tests: fixture (nodes + HOCON + variables + secrets) + query → expected JSON. Any divergence = regression in `spec.md` §4 pipeline.
- **No YobaConf self-config.** YobaConf configures itself from `appsettings.json` only. Do not read bootstrap settings from a YobaConf node (`$system/yobaconf` or otherwise) — it creates a bootstrap cycle. Master AES key is an environment variable.
- **No YobaLog → YobaConf dependency.** Events flow YobaConf → YobaLog via CLEF only. YobaLog's own config is `appsettings.json` (fixed in its own spec), so the reverse dependency is impossible by construction — don't accidentally add it.
- **Secrets in the audit log are always encrypted.** `spec.md` §7. Utility test: grep the `.db` file after writing a secret; plaintext must not be there.
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

## Working style for this repo

- When the user asks about "next step" or "what to do," check `doc/plan.md` — phases are the source of truth for ordering.
- Russian is fine in conversation and in decision-log entries; user-facing code strings are English ASCII (see plan invariants).
- This is a greenfield project — don't add backwards-compatibility shims, feature flags, or `// TODO: remove` comments. The spec and decision log are how we remember why things exist.
- **Plan update goes in the same commit as the feature.** When you complete or meaningfully shift a phase / bullet from `doc/plan.md`, update the file in the same commit.
- YobaConf is a sibling of YobaLog (`D:\my\prj\yobalog`). When making tooling/style/infra decisions, check there first — if it's solved, copy; if yobaconf needs to diverge, add a `decision-log.md` entry explaining why.
