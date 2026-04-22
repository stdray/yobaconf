# YobaConf: План работ и стратегия тестирования (v2, tagged model)

Spec — `doc/spec.md` (v2). Pivot rationale — `doc/decision-log.md` 2026-04-21 "Pivot to tagged bindings model".

## Ядро тестов — детерминизм resolve pipeline

Главный инвариант (spec §4): один и тот же вход (`tag-vector` + API key + состояние БД) обязан давать ровно один JSON-результат **или** 409 Conflict с известным diagnostic'ом. Покрывается **snapshot-тестами** (фикстура bindings + tag-vector → ожидаемый JSON/status) + **property-тестами** на subset-monotonicity (добавление more-specific binding'а никогда не ломает resolve для less-specific tag-vector'а).

Snapshot — первичный инструмент, property-тесты — для конфликт-lattice инвариантов.

## v1 deliverables, carried over

Инфра, которая переносится как есть (см. decision-log 2026-04-21 "Pivot"):

- [x] **AES-256-GCM encryption** — `AesGcmSecretEncryptor` + IV/AuthTag/KeyVersion fields (переместятся в Bindings rows с `Kind=Secret`).
- [x] **Admin cookie-auth** — `AdminPasswordHasher` (PBKDF2-SHA256 @100k), Login page, Razor cookie scheme.
- [x] **AuditLog semantics** — append-only, rollback через новый Upsert с `actor=restore:<id>`. Schema меняется (`TagSetJson` вместо `Path`), invariant тот же.
- [x] **OTLP tracing** wiring + `OpenTelemetry:Enabled` gating + `X-Seq-ApiKey` auth (reuse `YOBALOG_API_KEY`). Span names переопределяются на новые resolve-stages.
- [x] **Seq.Extensions.Logging** + `doc/logging-policy.md` — policy без изменений.
- [x] **Caddy deployment + CI** (`doc/deploy.md`, port 8081, GitHub Actions NuGet cache + merged `ci` job + BuildX GHA layer cache).
- [x] **Tailwind + DaisyUI dark theme** + `data-testid` selector invariant + bun build.
- [x] **Razor Pages + htmx** — UI framework.
- [x] **Import converters** — `JsonToHoconConverter` переименовывается в `JsonFlattener`, `YamlToHoconConverter` → `YamlFlattener`, `DotenvToHoconConverter` → `DotenvFlattener`. Output — `IReadOnlyList<(string KeyPath, JsonElement Value)>` вместо HOCON-текста. Regex-based per-leaf classification (v1 Phase B.7) — reused.
- [x] **`.NET SDK `YobaConf.Client`** — core HTTP polling + ETag infra переиспользуется. API shape переписывается: `AddYobaConf(opts => opts.WithTags(new { env = "prod", project = "yobapub" }))` вместо `opts.WithPath(...)`. Flattening to colon-keys — как есть.

## v2 Фазы

### Фаза A — Tagged MVP (dog-food ready)

Цель — реализовать subset-merge resolve + минимальный admin-UI так, чтобы можно было начать переключать консюмеров. Собирается из v1 compile-state (удаление HOCON/path-tree + новая имплементация в одном рабочем checkpoint'е; в git-history — отдельные commits).

- [ ] **A.0 Purge v1.** Удалить `src/YobaConf.Core/Hocon/`, `ResolvePipeline`/`IncludePreprocessor`/`HoconVariableRenderer`, `NodePath`, `HoconNode`, `Nodes`/`Variables`/`Secrets` entities, `/Index` (tree), `/Node` pages, `GET /v1/conf/{**urlPath}` endpoint, `ts/prism-hocon.ts`, `Hocon` + `Hocon.Configuration` packages из `Directory.Packages.props`. Все ссылающиеся тесты — удалить. Build должен зеленеть на пустом core (только `Crypto/`, `AdminPasswordHasher`, `ActivitySources`, `Observability/`).
- [ ] **A.1 Bindings storage.** `Binding` entity + `TagSet` value-object (canonical JSON serialization — ordinal key sort, no whitespace). `SqliteBindingStore` на linq2db: `Bindings` + `TagVocabulary` + `ApiKeys` + `AuditLog` tables (CREATE IF NOT EXISTS idempotent schema). Upsert/SoftDelete на `IBindingStoreAdmin`. Index: `UNIQUE (TagSetJson, KeyPath) WHERE IsDeleted=0`. WAL mode. Unit + integration тесты (bindings roundtrip, canonical-JSON byte-identical invariant, UNIQUE enforcement, soft-delete / resurrect).
- [ ] **A.2 Resolve pipeline.** `ResolvePipeline.Resolve(IReadOnlyDictionary<string,string> tagVector, IBindingStore) → ResolveResult { Json, ETag } | ConflictResult`. Stages: candidate-lookup (SQLite `json_each`-based subset predicate) → group-by-KeyPath → conflict-check (tied-at-max-specificity → 409) → decrypt-secrets (`ISecretEncryptor`) → expand-dotted → canonical JSON (`System.Text.Json`, ordinal-sorted) → sha256-16-hex ETag. 8 snapshot-тестов: happy path (0-tag root binding), single-dimension override, multi-dimension precedence, tied-identical-values (deterministic pick), tied-different-values → 409, secret decrypted on resolve, dotted-key expansion (`db.host` + `db.port` → `{"db":{...}}`), ETag determinism across binding-insert-order permutations.
- [ ] **A.3 ApiKeys.** `ApiKey` entity с `RequiredTagsJson` + `AllowedKeyPrefixes`. `X-YobaConf-ApiKey` header + `?apiKey=` query. Validation: `request.TagVector ⊇ apiKey.RequiredTags` (exact subset) → иначе 403. Filter response by `AllowedKeyPrefixes`. Constant-time token compare (`CryptographicOperations.FixedTimeEquals`). 6 integration-тестов (missing key / wrong hash → 401; subset mismatch → 403; prefix-filter subset; happy path).
- [ ] **A.4 `GET /v1/conf` endpoint.** Query-params → tag-vector. Slug validation на каждом `key=value`. Conflict → 409 JSON с `tiedBindings` array + hint. ETag + If-None-Match → 304. Integration-тесты через `WebApplicationFactory` (happy 200, 304 на match, 409 на incomparable, 403 apikey mismatch, 400 invalid slug).
- [ ] **A.5 Dashboard.** `/Bindings` — facet-filter bar (dropdown per known tag-key из TagVocabulary или free-form) + table (TagSet chips / Key / Value / Updated). Filter sugar: query-params → WHERE matching. Row click → detail view. Secret value masked `••••••` до reveal. Inline-add-row внизу таблицы. 4 E2E (filter by tag, search by key, reveal secret, add binding).
- [ ] **A.6 Binding editor.** Create / Edit panel: tag-picker (known keys из vocabulary + add-new), key input, value textarea / password input, Kind radio. Live preview: "Resolve with these tags would include this binding".  Inline conflict warning ("Incomparable with binding #X on key=Y; add overlay to disambiguate"). 3 E2E (create Plain, create Secret, edit with conflict detection).
- [ ] **A.7 `/Tags` page.** TagVocabulary CRUD. Free-form tags visible в warning-banner ("3 bindings use unknown tag 'contry' — typo?"). 2 E2E.
- [ ] **A.8 `/ApiKeys` page.** List + Create + Soft-delete. Generated token shown once on Create. 2 E2E.
- [ ] **A.9 OTel rewire.** Root span `yobaconf.resolve` + children `candidate-lookup` / `group-by-key` / `conflict-check` / `decrypt-secrets` / `expand-dotted` / `canonical-json` / `etag-compute`. SQLite spans: `sqlite.candidate-lookup` / `sqlite.upsert-binding` / etc. Attributes `yobaconf.tag-count` (specificity), `yobaconf.matched-count` (candidate count). Tests через `ActivityListener` с probe-scoped TraceId filter (pattern из v1 Phase C.5).

### Фаза B — Import + History + Rollback

Перевод v1 Phase B.6 + B.7 на tagged-storage. Sooner the better — paste-import сильно ускоряет onboarding существующих конфигов.

- [ ] **B.1 `/Import` paste flow.** Step 1: source textarea + format dropdown (JSON / YAML / `.env`) + target tag-picker. Step 2: classify table (per-leaf row: key / value / Plain|Secret radio). Step 3: Save → N bindings с тем же target tag-set. Preview по аналогии с v1. 4 E2E (json happy, yaml nested, dotenv, classify + save).
- [ ] **B.2 `/History` page.** Day-grouped timeline of AuditLog, filters (by tag-value, entity-type, actor, key-path substring). Per-entry Rollback → Upsert из `OldValue` с `actor=restore:<id>`. 3 E2E (list filter, rollback Plain, rollback Secret).

### Фаза C — Secrets hardening

`Kind=Secret` уже landed в Phase A как часть Bindings schema. Здесь — polish + rotation.

- [ ] **C.1 Secret reveal с single-read.** Reveal endpoint (не URL-param; POST с server-cached 10s window). Replace v1's data-attribute client-side hack. 2 E2E.
- [ ] **C.2 Master-key rotation.** CLI `--rotate-master-key <old> <new>` перечитывает все `Kind=Secret` bindings, decrypt old → encrypt new → KeyVersion bump. Audit entry на каждую row. 3 unit + 1 integration test.
- [ ] **C.3 Secret scope ACL** (если будет востребовано). Сейчас — API-key с путём доступа к bindings автоматически имеет доступ к decrypted secrets. Отдельная permission `CanReadSecrets` может быть добавлена если реальный use-case появится.

### Фаза D — Consumer SDKs

- [ ] **D.1 .NET SDK rewrite.** `AddYobaConf(opts => opts.Endpoint("...").WithTags(...).ApiKey("..."))` extension. `IConfigurationProvider` поверх HttpClient с ETag polling. Flattener: `{"db":{"host":"x"}}` → `db:host = x`. Fail-soft `Optional` flag. Existing v1 tests rewritten для tagged-tags вместо path. ~15 тестов.
- [ ] **D.2 Python SDK (Pydantic source).** async fetcher + Pydantic model validation + background refresh на ETag-change. Pytest coverage. Пакет `yobaconf` на PyPI (или private index).
- [ ] **D.3 TypeScript SDK (bun target).** Аналог Python'ового — async refresh + typed accessor. Пакет `@yobaconf/client` (или similar).

### Фаза E — Operational polish

- [ ] **E.1 Tag-priority escape hatch** (если конфликты станут частыми). `priority` column на TagVocabulary → tie-breaker при incomparable tie. Scoped за feature-flag, off by default — fail-fast остаётся primary behaviour.
- [ ] **E.2 Push integrations.** Экспорт resolved JSON в Redis/Consul/S3 для sverxy'nagruzok use-cases.
- [ ] **E.3 Read-replicas.** Litestream → read-only SQLite replicas если pet-scale прорастёт.
- [ ] **E.4 Perf benchmark.** Soft goal "p99 < 50ms resolve на 200 bindings × 5-dim tag-vector" — проверяется когда появится нагрузочный тест.

## Тестовое покрытие — приоритеты

- **Canonical JSON byte-identity.** Property test: `TagSet.Canonical({a:1,b:2}) == TagSet.Canonical({b:2,a:1})` → байт-идентичны. Blocking для UNIQUE index корректности.
- **Subset match SQL.** `json_each`-based predicate в `IBindingStore.FindMatching(tagVector)`. Case coverage: empty tag-vector matches только `{}`-tagged bindings; multi-tag vector matches strict subsets; tag-value mismatch excludes. ≥6 тестов.
- **Conflict lattice invariants** (property test): если два bindings incomparable и оба match'ят tag-vector с одинаковыми values → resolve picks deterministic (lowest Id), с разными → 409. Если хотя бы один subset другого → specificity wins.
- **ETag determinism.** Re-serialize → same hash. Shuffle binding-insert-order → same hash. Change any binding value → new hash.
- **Secrets at rest encrypted.** Utility-тест: записать Secret, grep `.db` на plaintext → 0 hits.
- **API-key subset semantics.** `RequiredTags={env:prod, project:yobapub}` + request `?env=prod&project=yobapub&region=eu` → 200. Request `?env=prod` → 403 (proper subset, но request не subset'ит required). Request `?env=staging&project=yobapub` → 403 (value mismatch).

## Инварианты, которые легко нарушить (читать перед кодом)

- **Нет self-config.** YobaConf конфигурируется только из `appsettings.json` + env vars. Ни одного binding'а в собственной БД, читаемого на startup'е. Мастер-ключ AES — env var `YOBACONF_MASTER_KEY`, не binding.
- **Нет YobaLog → YobaConf.** Единственное направление — YobaConf эмитит логи/трейсы в YobaLog (bootstrap cycle prevention).
- **Secrets в AuditLog — всегда зашифрованные.** Plaintext нигде не лежит в persistent storage. Только в resolve response и в transient admin-reveal (memory-cached 10s).
- **Slug-regex для tag-values и key-path сегментов.** `^[a-z][a-z0-9-]{0,39}$` + optional `$`-prefix для системных. Точка запрещена в сегменте (разделитель dotted-key).
- **API-ключ subset check — exact match по key+value.** Не by-key-only, не regex. `RequiredTags = {env:prod}` и request `?env=prod-v2` → 403.
- **Детерминизм resolve.** Incomparable tie + разные values → **409, не silent выбор**. Spec §4 invariant.
- **TagSet canonical JSON byte-identical.** Ordinal key sort. Любое отступление ломает UNIQUE index + ETag determinism.
- **Локализация с первого дня.** User-facing strings — literal English ASCII, через `IStringLocalizer`. CI non-ASCII check на `Pages/` + `ts/`.
- **UI-селекторы: `data-testid` обязателен.** Никаких `GetByText` / CSS / role-with-name.
- **Frontend build — Release-only.** `bun build` только в `$(Configuration) == Release` в MSBuild target. Dev — отдельный `bun run dev` watcher через `./build.sh --target=Dev`.
- **Никакого `IsEnvironment("Testing")` в production-коде.** Gates — config-driven. См. `decision-log.md` 2026-04-21 pattern.
- **Cake `DockerPush` depends on all test tasks explicitly.** Новый test-таргет → новый `.IsDependentOn(...)` на `DockerPush`.

## Перф-наблюдения из v1 prod-трейсов (follow-ups)

Observations из v1 stack; актуальны для v2 после переноса SqliteBindingStore на аналогичный pattern.

- [ ] **Cold-start первого SQLite-запроса.** v1 показал 133ms vs 1-8ms на subsequent'ах. Причина — `PRAGMA journal_mode=WAL` per-connection в `SqliteConfigStore.Open()`. Warmup в `/ready` handler нивелирует — либо WAL в ctor один раз.
- [ ] **N+1 в candidate-lookup.** v1 variable-resolve делал 6 запросов (2 per scope level). v2 tagged модели проще — один `WHERE subset` запрос на resolve, но если TagVocabulary query на каждое value-validation в UI начнёт прорастать — batch-fetch.

## Расщепление документации

- [x] `doc/spec.md` — v2 spec, pure (13 sections + migration + invariants).
- [x] `doc/plan.md` — этот файл, прогресс + тест-чеклист + инварианты.
- [x] `doc/decision-log.md` — лог архитектурных решений. Новые записи сверху.

## Открытые вопросы

- [ ] **Priority-flag на tag-key** — нужен ли эскейп-хэтч из fail-fast, или incomparable tie всегда ошибка дизайна (admin добавляет overlay)? Решение — после первых weeks прод-использования.
- [ ] **Facet-filter UX scale** — как выглядит dashboard с 200+ bindings и 5-dim tag-vocabulary? Virtual scroll? Group-by-tag-value? Решение — при прорастании беспокойства.
- [ ] **SDK — Python vs TS first?** Зависит от того, что ближайший consumer будет на чём написан. .NET первый в любом случае (`YobaConf.Client` уже существует, переписывается).
