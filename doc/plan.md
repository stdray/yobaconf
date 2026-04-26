# YobaConf: План работ и стратегия тестирования (v2, tagged model)

Spec — `doc/spec.md` (v2). Pivot rationale — `doc/decision-log.md` 2026-04-21 "Pivot to tagged bindings model". Scope-trim — `doc/decision-log.md` 2026-04-22 "Multi-admin, defer paste-import + vocab, add runner".

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
- [x] **DataProtection keys persistence** — `PersistKeysToFileSystem` на mounted volume (без этого cookie invalidates on redeploy, см. yobalog decision).
- [x] **Caddy deployment + CI** (`doc/deploy.md`, port 8081, GitHub Actions NuGet cache + merged `ci` job + BuildX GHA layer cache).
- [x] **Tailwind + DaisyUI dark theme** + `data-testid` selector invariant + bun build.
- [x] **Razor Pages + htmx** — UI framework.

## v2 Фазы

Каждая фаза собирается из мелких итераций, каждая — shippable checkpoint (компилится + все тесты зелёные). Deploy-тег не двигается до закрытия Phase B (т.е. пока нет end-to-end работающего MVP с admin-UI и resolve endpoint'ом).

### Фаза A — Storage + Resolve foundation

Цель — core engine: tagged storage + детерминированный subset-merge resolve + API-ключи. После закрытия фазы можно дог-фудить через `curl` без UI.

- [ ] **A.0 Purge v1.** Удалить `src/YobaConf.Core/Hocon/`, `ResolvePipeline`/`IncludePreprocessor`/`HoconVariableRenderer`, `NodePath`, `HoconNode`, `Nodes`/`Variables`/`Secrets` entities, `/Index` (tree), `/Node` pages, `GET /v1/conf/{**urlPath}` endpoint, `ts/prism-hocon.ts`, `Hocon` + `Hocon.Configuration` packages из `Directory.Packages.props`. Import converters **оставить** (`JsonToHoconConverter` etc.) — пригодятся когда paste-import прорастёт в Phase E. Все прочие ссылающиеся тесты удалить. Build должен зеленеть на пустом core (только `Crypto/`, `AdminPasswordHasher`, `ActivitySources`, `Observability/`, unused-но-сохранённые Import converters).
- [ ] **A.1 Bindings storage.** `Binding` entity + `TagSet` value-object (canonical JSON serialization — ordinal key sort, no whitespace). `SqliteBindingStore` на linq2db: `Bindings` + `ApiKeys` + `Users` + `AuditLog` tables (CREATE IF NOT EXISTS идемпотентно). Upsert/SoftDelete на `IBindingStoreAdmin`. Index: `UNIQUE (TagSetJson, KeyPath) WHERE IsDeleted=0`. WAL mode. Unit + integration тесты (bindings roundtrip, canonical-JSON byte-identical invariant, UNIQUE enforcement, soft-delete / resurrect) — минимум 8.
- [ ] **A.2 Resolve pipeline.** `ResolvePipeline.Resolve(IReadOnlyDictionary<string,string> tagVector, IBindingStore) → ResolveResult { Json, ETag } | ConflictResult`. Stages: candidate-lookup (SQLite `json_each`-based subset predicate) → group-by-KeyPath → conflict-check (tied-at-max-specificity → 409) → decrypt-secrets (`ISecretEncryptor`) → expand-dotted → canonical JSON (`System.Text.Json`, ordinal-sorted) → sha256-16-hex ETag. 8 snapshot-тестов: happy path (0-tag root binding), single-dimension override, multi-dimension precedence, tied-identical-values (deterministic pick), tied-different-values → 409, secret decrypted on resolve, dotted-key expansion, ETag determinism across binding-insert-order permutations.
- [ ] **A.3 ApiKeys model + validation.** `ApiKey` entity с `RequiredTagsJson` + `AllowedKeyPrefixes` + `TokenHash` + `TokenPrefix`. `SqliteApiKeyStore` + `IApiKeyAdmin` (create → plaintext shown once, hash stored; soft-delete; list). `X-YobaConf-ApiKey` header + `?apiKey=` query auth на resolve-endpoint. Validation: `request.TagVector ⊇ apiKey.RequiredTags` (exact subset) → иначе 403. Filter response by `AllowedKeyPrefixes`. Constant-time token compare (`CryptographicOperations.FixedTimeEquals`). 6 integration-тестов (missing key → 401; wrong hash → 401; subset mismatch → 403; prefix-filter subset; happy path; expired/soft-deleted → 401).
- [ ] **A.4 `GET /v1/conf` endpoint.** Query-params → tag-vector. Slug validation на каждом `key=value`. Conflict → 409 JSON с `tiedBindings` array + hint. ETag + If-None-Match → 304. `template=flat` (default, nested JSON response) — остальные templates в Phase C. 5 integration-тестов через `WebApplicationFactory` (happy 200, 304 на match, 409 на incomparable, 403 apikey mismatch, 400 invalid slug).
- [ ] **A.5 OTel rewire.** Root span `yobaconf.resolve` + children `candidate-lookup` / `group-by-key` / `conflict-check` / `decrypt-secrets` / `expand-dotted` / `canonical-json` / `etag-compute`. SQLite spans: `sqlite.candidate-lookup` / `sqlite.upsert-binding` / etc. Attributes `yobaconf.tag-count` (specificity), `yobaconf.matched-count` (candidate count). Tests через `ActivityListener` с probe-scoped TraceId filter.

### Фаза B — Admin UI (minimum shippable)

Цель — Razor-pages интерфейс чтобы admin мог залогиниться, создать users / api-keys / bindings без `curl`'а. После закрытия — **первый deploy**, переключение прод-consumer'ов на v2.

- [ ] **B.1 Login page + cookie auth.** `SqliteUserStore` + `IUserAdmin` на linq2db. `LoginModel` — пустая DB → config-admin fallback (bootstrap path), иначе DB-only. Dummy-verify на miss-path (constant-time защита от user-enumeration). Pattern из yobalog. Antiforgery-токен на форме. Rate-limit — placeholder counter, без enforcement до прорастания attack'а. 4 E2E (happy login, wrong password, empty DB + config-fallback works, antiforgery-miss → 400).
- [ ] **B.2 `/admin/users` CRUD page.** Create (username + password → hash), list, rotate password (separate action), delete через confirm. Block delete последнего user'а (иначе config-fallback просыпается, часто unintended). 3 E2E.
- [ ] **B.3 `/admin/api-keys` CRUD page.** Create-form (description + required-tags picker + allowed-key-prefixes textarea) → plaintext-token shown **once**, hash stored. List с prefix-display. Soft-delete через confirm. Validation: admin не может создать key с `RequiredTags = {}` без confirm (это superuser-token). 3 E2E.
- [ ] **B.4 `/Bindings` dashboard.** Facet-filter bar — tag-key dropdown'ы из `SELECT DISTINCT` по actually-used values (без явной vocabulary). Table columns: TagSet chips, Key, Value (secrets маскированы), Updated, Actions. Row click → edit panel. Inline-add-row в конце таблицы. 4 E2E (filter by tag, search by key, reveal secret inline, add new binding).
- [ ] **B.5 Binding editor.** Create / Edit panel: tag input array (key + value + autocomplete из existing values), key input, value textarea / password input, Kind radio. Live preview: "Resolve for these tags would include this binding". Inline conflict warning ("Incomparable with binding #X on key=Y; add overlay to disambiguate"). 3 E2E (create Plain, create Secret, edit with conflict detection).
- [ ] **B.6 First deploy checkpoint.** Smoke-test на prod VM через `/v1/conf?...`. Переключить 1-2 реальных consumer'а (запуск через `curl` exported env → shell script-runner) чтобы поймать surprise integration-issue до Phase C SDK. Deploy-тег передвигается после этого.

### Фаза C — Consumer runtime (.NET SDK)

Цель — `YobaConf.Client` SDK поверх `IConfigurationProvider` с ETag polling и hot-reload, чтобы `IOptionsSnapshot` / `IOptionsMonitor` перечитывали конфиг без restart'а процесса. Runner-based ingestion (`yobaconf-run` sidecar CLI) отложен — см. "Открытые вопросы" ниже.

- [x] **C.1 Alias templates + response shapes.** `template` query-param на `/v1/conf`: `flat` (default, current nested JSON), `dotnet` (`db__host=…`), `envvar` (`DB_HOST=…`), `envvar_deep` (`DB__HOST=…`). Server applies transformation **после** expand-dotted stage. Per-binding `Aliases` column (JSON dict `{templateName: aliasName}`) для override — fallback на template-derivation. 6 snapshot-тестов (4 templates × flat + 2 alias-override cases). Остаётся полезным и в SDK-only мире: server-side DRY для будущих multi-lang SDK.
- [ ] **C.2 .NET SDK.** `YobaConf.Client` — `AddYobaConf(opts => opts.Endpoint("...").WithTags(...).ApiKey("..."))` extension. `IConfigurationProvider` поверх HttpClient с ETag polling. Flattener: response → `db:host = x` colon-keys (standard .NET Configuration convention). Fail-soft `Optional` flag. 12 тестов (happy roundtrip, ETag 304, connection retry, bad key → Exception if Required, silent if Optional, tag-var overrides, reload on change).

### Фаза D — Audit + History + Rollback

Цель — observability over config changes: кто что когда менял, можно ли откатить.

- [ ] **D.1 AuditLog wiring.** Append-entry на каждое Upsert/SoftDelete/Restore в Bindings / ApiKeys / Users. Actor = cookie-auth username OR `restore:<id>` OR `system` (bootstrap). No direct writes to AuditLog — only storage impls populate. 5 unit tests (one per entity × CRUD, rollback writes fresh audit row).
- [ ] **D.2 `/History` page.** Day-grouped timeline of AuditLog, filters (by tag-value for bindings, entity-type, actor, key-path substring). Per-entry Rollback action → Upsert из `OldValue` с `actor=restore:<id>`. Block rollback если target уже не существует (soft-deleted + not-resurrectable because schema conflict). 4 E2E (list filter by actor, filter by entity, rollback Plain, rollback Secret).

### Фаза G — Admin API (scripting / automation)

Цель — JSON-эндпоинты за token-auth, чтобы owner мог автоматизировать массовые операции (выставить 14 значений для одного tag-set, обновить один key в трёх env'ах через for-loop, ротировать набор runtime-ключей) без UI-кликов. Параллельна Phase C/D, не блокирует first deploy — стартует по реальному pain'у при работе через UI.

- [x] **G.1 `AdminToken` entity + `SqliteAdminTokenStore`.** Новая таблица `AdminTokens(Id, Username, TokenHash UNIQUE, TokenPrefix, Description, UpdatedAt, IsDeleted)` — `Username` plain TEXT (без жёсткого SQL FK, consistency через handler-логику; так же делает остальной yobaconf). Token shape — `ApiKeyTokenGenerator.New()` (22-char base64url, 122 бита энтропии); storage — sha256-hex hash + 6-char prefix, идентично `SqliteApiKeyStore`. **Не reuse `ApiKeys`-таблицы**: runtime-keys (RequiredTags + AllowedKeyPrefixes для resolve) и admin-tokens (full-CRUD от лица user'а) — разные scope'ы; single-table-with-flag (`IsAdminToken bool`) ведёт к багам класса "забыл проверить флаг — admin token прошёл валидацию ConfEndpoint'а как runtime-key". Type-safety через separate tables — тот же rationale что Variables vs Secrets в v1. UNIQUE-индекс на `TokenHash` (где `IsDeleted=0`). `IsDeleted` flag используется **только для self-revoke** через `/Admin/Profile`; на `IUserAdmin.Delete(username)` — **hard-delete** all tokens одной транзакцией с hard-delete user'а (Users итак hard-delete'ятся; token-zombie с висящей привязкой бесполезен — реактивации нет, audit-log хранит actor строкой). UI user-delete confirm-dialog должен показать N active tokens. 6 тестов (create roundtrip, lookup-by-hash, self-revoke soft-delete, list-by-username, hard-delete cascade на IUserAdmin.Delete, soft-deleted token не валидируется).
- [x] **G.2 Token-auth middleware.** Поддерживаемые формы (любая одна; конфликт двух разных значений → 400 `ambiguous_auth`):
    - `Authorization: Bearer <token>` — primary; HTTP-стандарт, работает с generic-клиентами (`HttpClient.DefaultRequestHeaders.Authorization`, `curl -H "Authorization: Bearer $T"`, OpenAPI-генерируемые SDK).
    - `X-YobaConf-AdminToken: <token>` — equivalent; явное именование канала, симметрично runtime-keys'овому `X-YobaConf-ApiKey`.
    - `?adminToken=<token>` query — fallback для quick-curl и легаси-клиентов; precedence ниже header'ов.

    Constant-time compare через `CryptographicOperations.FixedTimeEquals`. Resolve token → User → выставить `HttpContext.User` identity (cookie-equivalent), чтобы все downstream-handler'ы (audit-actor enricher, IUserAdmin guards) работали без отдельных code paths. Mounting на `/v1/admin/*` через endpoint convention или `[AdminAuthorize]` filter. Missing/invalid/soft-deleted/orphan-username (user удалён, токен ещё не cascade'нут — race window) → 401 JSON `{error, reason}`. 7 integration-тестов (Bearer happy, X-YobaConf-AdminToken happy, query happy, missing → 401, wrong → 401, soft-deleted → 401, Bearer + X-YobaConf-AdminToken с разными значениями → 400, cookie-session НЕ заменяет admin-token и наоборот).
- [x] **G.3 `/v1/admin/bindings` endpoints.**
    - `PUT /v1/admin/bindings` body `{tagSet, keyPath, kind, value}` → upsert (create-or-update идемпотентно по (TagSet, KeyPath)). Slug-validation на каждый tag-key/value + KeyPath segment; `kind=Secret` → encrypt через `ISecretEncryptor` перед записью. Возвращает `{id, etag, created: bool}`. Audit actor — `<Username>:admin-token:<TokenPrefix>`.
    - `DELETE /v1/admin/bindings/{id}` → soft-delete. Audit row автоматически через `IBindingStoreAdmin`.
    - `GET /v1/admin/bindings?tag=k=v&key=prefix*` → JSON array; для `Kind=Secret` value поле — `null` + `kind: "Secret"` (plaintext-секреты не leak'ают через admin-list — отдельный reveal flow в Phase E.3 уже решает single-read pattern).
    - 6 integration-тестов (PUT create, PUT update existing → same Id, DELETE soft-deletes + audit row, GET filter by tag-key+value, GET filter by key prefix, secret value redacted в list).
- [x] **G.4 `/v1/admin/api-keys` endpoints.** `PUT /v1/admin/api-keys` body `{description, requiredTags, allowedKeyPrefixes?}` → возвращает `{id, plaintext, prefix}` **один раз** (как UI). `DELETE /v1/admin/api-keys/{id}` → soft-delete. `GET /v1/admin/api-keys` → list без plaintext'ов (только prefix + scope + description). 4 integration-теста.
- [x] **G.5 `/Admin/Profile` Razor-page — token CRUD.** UI для текущего залогиненного user'а: list своих admin-tokens, "New token" form (description-only, scope не настраивается — full-rights tied to user) → plaintext shown один раз с copy-to-clipboard (reuse паттерна F.2 для api-keys), "Revoke" row-action → soft-delete с confirm. Tokens строго per-user — нельзя видеть/отзывать чужие (даже cookie-сессия другого admin'а не видит чужих токенов в этом UI). 3 E2E (create + plaintext shown once + copy works, revoke removes from list, list isolated per session-user).
- [x] **G.6 `doc/admin-api.md`.** Curl-примеры под типичные use case'ы: (1) выставить 14 значений для `{env=prod, project=yobapub}` через `for k in ...; do curl -X PUT ...; done`; (2) обновить `db.host` для `env in {dev,staging,prod}`; (3) ротация runtime-ключа (DELETE old + PUT new); (4) list bindings filtered by tag для backup-script'а. Reference на token-creation flow (`/Admin/Profile`). Cross-link из README + `doc/spec.md` §8.

Deferred внутри Phase G (включаются только на доказанный pain, не auto-следующая итерация):

- **`POST /v1/admin/bindings:bulk` — atomic batch upsert.** Pet-scale (≤200 bindings): sequential PUT × 14 ≈ секунда total на pet-VM, atomicity на batch-уровне нужна реально только для миграций / restore (E.1 paste-import уже планирует свой собственный transactional bulk insert). Преждевременно.
- **`POST /v1/admin/rollback/{auditId}`.** Domain-логика rollback'а реализуется в Phase D.2; admin-API endpoint становится тонкой JSON-обёрткой над тем же `IBindingStoreAdmin.Restore`, добавляется в G.3 одной задачей после закрытия D.
- **Users CRUD через admin-API.** User-management — sensitive операция; через UI с confirm-диалогами достаточно. Если scripting реально понадобится (массовая ротация паролей в incident response) — добавить отдельным item'ом G.7.
- **Per-token scope (TagSet-restricted admin tokens).** В MVP token = full-rights user-equivalent. Scoped admin tokens (owner делегирует subset bindings shared editor'у) — это часть RBAC (см. open question "RBAC: super-admin vs scoped user"); решается одним дизайн-spike'ом совместно с `User.Scope`.
- **Token last-used timestamp / per-token rate-limit.** Полезно для отладки "какой скрипт юзает этот токен", но не блокирует use case. Single-admin-multi-machine — лоу-приоритет.

### Фаза E — Deferred (post-MVP, по мере спроса)

Не блокирует dog-food использование. Приоритет — по реальному pain'у, не по плану.

- [ ] **E.1 Paste-import `/Import` page.** Step 1: source textarea + format dropdown (JSON / YAML / `.env`) + target tag-picker. Step 2: classify table (per-leaf row: key / value / Plain|Secret radio). Step 3: Save → N bindings с общим target tag-set. Импорт-converter'ы (`JsonFlattener` etc.) переиспользуются из v1 (сохранены в A.0). 4 E2E.
- [ ] **E.2 TagVocabulary + `/Tags` page.** `TagVocabulary(TagKey, AllowedValues, IsRequired, Description)` table. CRUD UI на `/Tags`. Warning-banner в `/Bindings` editor если binding использует unknown tag-key. 2 E2E.
- [x] **E.3 Secret reveal single-read.** POST-endpoint с server-cached 10s window. Audit entry per reveal. Замена client-side reveal через data-attribute. 2 E2E.
- [x] **E.5 Tag-priority escape hatch.** `priority` column на TagVocabulary → tie-breaker при incomparable tie. Feature-flag off by default — fail-fast остаётся primary.
- [ ] **E.6 Python SDK.** `pip install yobaconf` — async fetcher + Pydantic model validation + background ETag refresh. Pytest coverage.
- [ ] **E.7 TypeScript SDK.** `@yobaconf/client` для bun/node — async refresh + typed accessor.
- [ ] **E.8 Push integrations.** Export resolved JSON в Redis / Consul / S3 для sverxy-нагрузки use-cases.
- [ ] **E.9 Read-replicas.** Litestream → read-only SQLite replicas.
- [x] **E.10 Perf BDN.** `benchmarks/YobaConf.Benchmarks/` — resolve pipeline под 200 bindings × 5-dim tag-vector, target p99 < 50ms.
- [ ] **E.11 Master-key rotation CLI.** `yobaconf --rotate-master-key <old> <new>` — decrypt all `Kind=Secret` bindings с old, encrypt с new, bump `KeyVersion`, audit row per binding. 3 unit + 1 integration. **Priority: tail of Phase E** — админские CLI-утилиты актуализируются только когда появится реальный инцидент-триггер (compromised key), а не по плану.

## Тестовое покрытие — приоритеты

- **Canonical JSON byte-identity.** Property test: `TagSet.Canonical({a:1,b:2}) == TagSet.Canonical({b:2,a:1})` → байт-идентичны. Blocking для UNIQUE index корректности.
- **Subset match SQL.** `json_each`-based predicate в `IBindingStore.FindMatching(tagVector)`. Case coverage: empty tag-vector matches только `{}`-tagged bindings; multi-tag vector matches strict subsets; tag-value mismatch excludes. ≥6 тестов.
- **Conflict lattice invariants** (property test): если два bindings incomparable и оба match'ят tag-vector с одинаковыми values → resolve picks deterministic (lowest Id), с разными → 409. Если хотя бы один subset другого → specificity wins.
- **ETag determinism.** Re-serialize → same hash. Shuffle binding-insert-order → same hash. Change any binding value → new hash.
- **Secrets at rest encrypted.** Utility-тест: записать Secret, grep `.db` на plaintext → 0 hits.
- **API-key subset semantics.** `RequiredTags={env:prod, project:yobapub}` + request `?env=prod&project=yobapub&region=eu` → 200. Request `?env=prod` → 403 (proper subset, но request не subset'ит required). Request `?env=staging&project=yobapub` → 403 (value mismatch).
- **Alias template roundtrip (Phase C).** Input `{db.host: x, db.port: 5432}` × template → expected output mapping, 4 templates × snapshot.

## Инварианты, которые легко нарушить (читать перед кодом)

- **Нет self-config.** YobaConf конфигурируется только из `appsettings.json` + env vars. Ни одного binding'а в собственной БД, читаемого на startup'е. Мастер-ключ AES — env var `YOBACONF_MASTER_KEY`, не binding.
- **Нет YobaLog → YobaConf.** Единственное направление — YobaConf эмитит логи/трейсы в YobaLog (bootstrap cycle prevention).
- **Secrets в AuditLog — всегда зашифрованные.** Plaintext нигде не лежит в persistent storage. Только в resolve response и в transient admin-reveal (memory-cached 10s).
- **Slug-regex для tag-values и key-path сегментов.** `^[a-z][a-z0-9-]{0,39}$` + optional `$`-prefix для системных. Точка запрещена в сегменте (разделитель dotted-key).
- **API-ключ subset check — exact match по key+value.** Не by-key-only, не regex. `RequiredTags = {env:prod}` и request `?env=prod-v2` → 403.
- **Multi-admin — симметричные права.** В MVP: Users-table admin == config-admin == "имеет всё". Любой role-split — post-MVP через `Role` column.
- **Детерминизм resolve.** Incomparable tie + разные values → **409, не silent выбор**. Spec §4 invariant.
- **TagSet canonical JSON byte-identical.** Ordinal key sort. Любое отступление ломает UNIQUE index + ETag determinism.
- **Alias template — server-side transformation.** Клиент шлёт `?template=dotnet`, сервер возвращает готовые `db__host` / `db:host` / `DB_HOST` пары согласно template'у. Никакой client-side адаптации (иначе каждый SDK повторяет логику с drift'ом).
- **Локализация с первого дня.** User-facing strings — literal English ASCII, через `IStringLocalizer`. CI non-ASCII check на `Pages/` + `ts/`.
- **UI-селекторы: `data-testid` обязателен.** Никаких `GetByText` / CSS / role-with-name.
- **Frontend build — Release-only.** `bun build` только в `$(Configuration) == Release` в MSBuild target. Dev — отдельный `bun run dev` watcher через `./build.sh --target=Dev`.
- **Никакого `IsEnvironment("Testing")` в production-коде.** Gates — config-driven. См. `decision-log.md` 2026-04-21 pattern.
- **Cake `DockerPush` depends on all test tasks explicitly.** Новый test-таргет → новый `.IsDependentOn(...)` на `DockerPush`.

## Перф-наблюдения из v1 prod-трейсов (follow-ups)

Observations из v1 stack; актуальны для v2 после переноса SqliteBindingStore на аналогичный pattern.

- [x] **Cold-start первого SQLite-запроса.** v1 показал 133ms vs 1-8ms на subsequent'ах. Причина — `PRAGMA journal_mode=WAL` per-connection в `SqliteConfigStore.Open()`. Warmup в `/ready` handler нивелирует — либо WAL в ctor один раз.

  Moved `PRAGMA journal_mode=WAL;` out of per-call `Open()` into one-time `SqliteSchema.EnsureSchema()` (schema-init phase, before reading `user_version`). WAL persists at DB-file level so subsequent connections inherit it automatically.
- [ ] **N+1 в candidate-lookup.** v1 variable-resolve делал 6 запросов (2 per scope level). v2 tagged модели проще — один `WHERE subset` запрос на resolve.

## Ревизия UI / post-Phase-B фидбек (2026-04-24)

Собрано после прохода по коду и работе с интерфейсом. Небольшие фиксы и UX-добавки, не требующие новой архитектуры. Приоритет — по удобству для owner'а, не последовательный.

- [x] **F.1 Stale баннер на главной.** Заменён на краткое описание YobaConf + быстрые ссылки на /Bindings, /Admin/Users, /Admin/ApiKeys, /History, /Tags.
- [ ] **F.2 Copy-to-clipboard для plaintext-token.** После создания API-ключа токен показывается один раз ("Save this token now — it will not be shown again"). Добавить кнопку "Copy" рядом с полем. TS-модуль на `navigator.clipboard.writeText`, fallback на `document.execCommand` не нужен (internal tool). 1 E2E.
- [ ] **F.3 Разделить `/admin/api-keys` на `/list` + `/create`.** Сейчас обе функции на одной странице, что плохо масштабируется. List-страница: таблица с фильтрами (prefix, required-tags), поиском по description, пагинацией (server-side, 50/page). Create — отдельная форма. Soft-delete через row-action на list.
- [ ] **F.4 Delete binding на `/Bindings`.** `IBindingStoreAdmin.SoftDelete(id, at, actor)` уже есть; UI action отсутствует. Row-action "Delete" на /Bindings → confirm-dialog → SoftDelete → AuditLog entry (автоматически через storage). 1 E2E (create → delete → gone from list, audit row есть).
- [ ] **F.5 Bindings pagination audit.** spec §6 закладывал ≤200 rows "pet-scale". Проверить: сейчас / на /Bindings — что происходит при 500? Если performance/usability деградирует — добавить server-side paging (50/page) + сохранение filter state в query-string. Решение — замерить сначала, потом паджинировать.
- [ ] **F.6 Preview тэг-комбинации.** На форме создания API-ключа: после заполнения `RequiredTags` — "Preview" button → показывает, какой JSON вернёт `/v1/conf?<tags>&apiKey=<new>` на текущих bindings. Альтернатива: отдельная `/Preview` страница с формой tag-vector → resolved JSON (с conflict-diagnostic если 409). Использует `ResolvePipeline.Resolve`, не HTTP call.
- [ ] **F.7 Footer с версией.** `_Layout.cshtml` — маленький footer: `v{APP_VERSION} · {GIT_SHORT_SHA} · built {BUILD_DATE}`. Env-vars уже пробрасываются (`Program.cs` читает `APP_VERSION` / `GIT_SHORT_SHA` для CLEF-enrichers); нужно только render. Зеркалит yobalog pattern — если там есть, скопировать.
- [x] **F.8 "ASCII-only" vs "non-Latin script".** Переформулировано в AGENTS.md: разрешены Basic Latin + пунктуация + emoji (👁, •), запрещены буквы не-латиницы (кириллица, иврит, арабица, сирияка). `&#x2022;` / `&#128065;` в `Pages/Bindings/Index.cshtml` заменены на литералы `•` / `👁`.

## Расщепление документации

- [x] `doc/spec.md` — v2 spec (15 sections + migration + invariants).
- [x] `doc/plan.md` — этот файл, прогресс + тест-чеклист + инварианты.
- [x] `doc/decision-log.md` — лог архитектурных решений. Новые записи сверху.
- [ ] `doc/consumer-integration.md` — how-to для consumer'ов (создаётся в C.4).

## Открытые вопросы

- [ ] **Priority-flag на tag-key** — нужен ли эскейп-хэтч из fail-fast, или incomparable tie всегда ошибка дизайна (admin добавляет overlay)? Решение — после первых weeks прод-использования.
- [ ] **Facet-filter UX scale** — как выглядит dashboard с 200+ bindings и 5-dim tag-vocabulary? Virtual scroll? Group-by-tag-value? Решение — при прорастании беспокойства.
- [ ] **Consumer runtime — SDK-первый vs runner-первый.** `yobaconf-run` sidecar CLI (fetch → env-export → exec child) — snapshot-only модель: конфиг не перечитывается без restart'а контейнера. Реальные сценарии владельца требуют hot-reload через `IOptionsSnapshot` / `IOptionsMonitor` в ASP.NET, с запретом на Serilog-подобные библиотеки, кэширующие конфиг на startup'е. SDK (`YobaConf.Client` + `IConfigurationProvider` с ETag polling) решает это из коробки. Runner может иметь место для не-.NET сервисов или zero-coupling Docker entrypoint'а — но пока не основной путь. Решение — после нескольких weeks реального SDK-использования. Если runner оживёт: отдельный открытый под-вопрос — transport fallback при недоступности `/v1/conf` на startup'е (retry / fail / cached-response-file).
- [ ] **Multi-value required-tags на API ключе.** Сейчас `ApiKey.RequiredTags` — это `TagSet` (dict, один value на key): нельзя одним ключом покрыть `env=dev1` **и** `env=dev2`. Owner хочет такого кейса (2026-04-24 ревизия, п.4). Варианты: (а) RequiredTags → `Dictionary<string, IReadOnlyList<string>>` с OR-семантикой внутри key'а, AND между key'ями; (б) оставить strict TagSet + явная доктрина "один ключ = одна комбинация"; (в) N ключей с общим description-prefix + list-view их группирует. Выбор влияет на subset-check в `ConfEndpoint` и UI создания ключа. Решение — после выбора (а) vs (в), дизайн отдельной записью в decision-log.
- [ ] **RBAC: super-admin vs scoped user.** Сейчас Users-табличка плоская — любой залогиненный админ может всё (создавать TagVocabulary, менять любой binding, crypto-ключи). Owner хочет (2026-04-24 ревизия, п.8): super-admin → полный доступ + права на vocabulary/tags; scoped user → правка bindings/values только внутри assigned scope (по аналогии с `ApiKey.RequiredTags`). Это новая доменная ось — поле `User.Role` + поле `User.Scope` (TagSet) + проверки на каждом admin-handler'е + UI создания пользователя с scope-picker'ом. Скоуп фазы — B+ / отдельная фаза. Требует дизайна: (1) как scope проверяется при Upsert'е binding'а чей TagSet не sub/super-set scope'а user'а; (2) как делать наследование (owner может делегировать subset); (3) migration для существующих users (все → super-admin до первого явного downgrade). Решение — отдельный doc spike перед implementation.
