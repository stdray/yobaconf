# YobaConf — Specification (tagged model, v2)

Центральный self-hosted сервис конфигурации для small-scale стека сервисов одного владельца (5-10 consumer'ов). Admin пишет значения через web-UI, runtime-сервисы тянут resolved JSON по HTTP. **Модель — tagged bindings**: конфиг хранится как плоские `(tag-set, key, value)` triple'ы, resolve — subset-merge с детерминированной обработкой конфликтов.

Этот spec — вторая версия. v1 был path-tree с HOCON-as-storage; отказались 2026-04-21 из-за реального multi-dimensional use-case'а + отпавшей необходимости в HOCON (см. `decision-log.md` "Pivot to tagged model"). Phase A+B path-tree-код живёт в git-history до момента реализации v2 — как ref-имплементация для audit/encryption/tracing/CI (которые переносятся как есть).

## 1. Архитектурные принципы

- **Multi-admin, pet-scale.** 5-10 consumer'ов, ≤200 bindings, ≤5 measurement-осей. Не multi-tenant SaaS. Несколько админов возможны — права симметричны (как с API-ключами: у всех полный доступ, никаких role-split'ов в MVP).
- **Tagged bindings, не hierarchy.** Конфиг — плоская таблица `(tag-set, key, value)`. Никаких иерархических путей, никакого "дерева нод".
- **Subset-merge resolve.** Consumer шлёт tag-vector; сервер находит все bindings с `TagSet ⊆ request.TagVector`, мержит по specificity (больше tags = выше приоритет), возвращает canonical JSON.
- **Fail-fast на incomparable конфликтах.** Если две incomparable (ни одно не subset другого) bindings определяют одинаковый key и обе match'ят request — resolve возвращает 409 Conflict с явным diagnostic'ом. Admin добавляет more-specific overlay для disambiguation. Priority-based escape hatch — deferred (см. §14).
- **HOCON выкинут.** Storage — flat triple'ы. Никакого HOCON parser'а ни на чтении, ни на записи. Paste-import в форматах JSON / YAML / `.env` — deferred (см. §14).
- **Секреты — kind'ом binding'а.** Не отдельная сущность; AES-256-GCM encrypted ciphertext в тех же Bindings-rows с `Kind=Secret`. Resolve decrypt'ит автоматически.
- **Consumer runtime — env-vars через runner.** Основной способ потребления — sidecar CLI `yobaconf-run` фетчит resolved JSON, экспортит в переменные среды по template'у (`.NET` → `Double__Underscore`, POSIX → `UPPER_SNAKE`, etc.) и запускает child-процесс. Docker entrypoint-integration первого класса.
- **Immutable audit.** Каждая запись — строка в AuditLog с old/new value + hash, append-only. Rollback = upsert из previous-value.
- **yobaconf → yobalog однонаправленно.** yobaconf шлёт свои события в yobalog через CLEF endpoint. Обратное запрещено (bootstrap cycle).

## 2. Технологический стек

- **Платформа:** .NET 10 (монолит, Razor Pages + htmx).
- **База данных:** SQLite через `linq2db.SQLite.MS` (синхронно с yobalog'овским стеком — единый tooling). Single-file `.db`, WAL mode.
- **Tag-сет хранение:** canonical-JSON TEXT column + `json_each`/`json_extract` из SQLite JSON1 для subset-match запросов. Индекс на `(KeyName, TagSet_canonical)`.
- **Сериализация:** `System.Text.Json` для canonical ordinal-sorted JSON (ETag determinism).
- **Конфигурация самого сервиса:** `appsettings.json` + env vars. yobaconf **не может** конфигурироваться через yobaconf (bootstrap cycle).
- **Наблюдаемость:**
    - Логи: `Seq.Extensions.Logging` → yobalog's Seq-compat endpoint. Static-field enrichers (App/Env/Ver/Sha/Host) через `AddSeq(enrichers: [...])`. Policy — `doc/logging-policy.md`.
    - Трейсы: OpenTelemetry OTLP HTTP/Protobuf → yobalog's `/v1/traces`. ActivitySources: `YobaConf.Resolve`, `YobaConf.Storage.Sqlite`.
- **Workspace-уровень отсутствует.** Изоляция между проектами/командами — через tag-filter'ы в API-key'ах.

## 3. Модель данных

### Bindings — единая core-сущность

Заменяет прежние Nodes + Variables + Secrets. Каждый row — одно (tag-set, key, value) binding.

```sql
CREATE TABLE Bindings (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    TagSetJson   TEXT    NOT NULL,   -- canonical JSON: {"env":"prod","project":"yobapub"}
    TagCount     INTEGER NOT NULL,   -- denormalized specificity
    KeyPath      TEXT    NOT NULL,   -- dotted: "db.host", "cache.policy.lru"
    ValuePlain   TEXT    NULL,       -- Kind=Plain; JSON-encoded scalar ("\"prod\"" / "42" / "true" / "null")
    Ciphertext   BLOB    NULL,       -- Kind=Secret
    Iv           BLOB    NULL,
    AuthTag      BLOB    NULL,
    KeyVersion   TEXT    NULL,       -- Secret master-key version ("v1", "v2", ...)
    Kind         TEXT    NOT NULL,   -- "Plain" | "Secret"
    ContentHash  TEXT    NOT NULL,   -- sha256 hex of (ValuePlain or Ciphertext)
    UpdatedAt    INTEGER NOT NULL,   -- unix ms
    IsDeleted    INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX ux_bindings_tagset_key_live
    ON Bindings(TagSetJson, KeyPath) WHERE IsDeleted = 0;

CREATE INDEX ix_bindings_key
    ON Bindings(KeyPath) WHERE IsDeleted = 0;
```

**TagSet canonicalization:** JSON с ключами отсортированными lexicographically, no whitespace. Два tag-set с одинаковым содержимым получают байт-идентичный TagSetJson → UNIQUE index работает.

**KeyPath:** dotted form (`db.host`), splits на nested JSON при resolve. Slug каждого сегмента: `^[a-z][a-z0-9-]{0,39}$` + optional `$`-prefix для системных (`$schema`, `$meta`).

**Tag-values:** тот же slug regex что KeyPath сегменты. `env=prod`, `project=yobapub`, `region=eu-west`. Lowercase + dash, URL-safe.

### Users — админы

Cookie-auth admin'ы. В MVP все права у всех равные — один и тот же scope, что и у server-wide "root" API-ключа: полный CRUD на bindings / api-keys / users / audit-log. Role-split'ы (read-only / audit-only) — post-MVP если реально понадобится.

```sql
CREATE TABLE Users (
    Username      TEXT    PRIMARY KEY,
    PasswordHash  TEXT    NOT NULL,  -- PBKDF2-HMAC-SHA256, 600k iter
    CreatedAt     INTEGER NOT NULL
);
```

`LoginModel.AuthenticateAsync` flow: если `Users` пусто → fallback на config-admin (`Admin:Username` + `Admin:PasswordHash` в appsettings) — bootstrap path; иначе только DB (config игнорируется до удаления последнего DB-юзера). Dummy-verify на miss-path для constant-time защиты от user-enumeration по тайминам. Паттерн заимствован из yobalog (`SqliteUserStore` + `AdminPasswordHasher`).

### ApiKeys

```sql
CREATE TABLE ApiKeys (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    TokenHash           TEXT    NOT NULL,
    TokenPrefix         TEXT    NOT NULL,   -- first 6 chars for UI display
    RequiredTagsJson    TEXT    NOT NULL,   -- canonical JSON
    AllowedKeyPrefixes  TEXT    NULL,       -- JSON array ["db.","cache."] или NULL для unrestricted
    Description         TEXT    NOT NULL,
    UpdatedAt           INTEGER NOT NULL,
    IsDeleted           INTEGER NOT NULL DEFAULT 0
);
```

Validation rules:
- Request `X-YobaConf-ApiKey` matches `TokenHash` → identify key.
- `request.TagVector` must contain every entry of `RequiredTagsJson` с exact value match (subset-superset).
- Если `AllowedKeyPrefixes` non-null → вернуть только те keys, которые prefix-match любому из allowed. Consumer получает **subset** bindings (фильтрация на уровне resolve, не ошибка).

### AdminTokens — personal access tokens

Per-user токены для scripting / automation поверх admin-API (`/v1/admin/*`, см. §8). Семантически **роль user'а через header**, не runtime-ключ — отдельная таблица, не reuse `ApiKeys` (см. `decision-log.md` 2026-04-26 "Admin API: personal admin tokens").

```sql
CREATE TABLE AdminTokens (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Username     TEXT    NOT NULL REFERENCES Users(Username),
    TokenHash    TEXT    NOT NULL,
    TokenPrefix  TEXT    NOT NULL,   -- first 6 chars for UI display
    Description  TEXT    NOT NULL,
    UpdatedAt    INTEGER NOT NULL,
    IsDeleted    INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX ux_admin_tokens_hash_live
    ON AdminTokens(TokenHash) WHERE IsDeleted = 0;
```

Token shape — тот же `ApiKeyTokenGenerator` (22-char base64url, 122 бита). Storage — sha256-hex hash + 6-char prefix. Plaintext возвращается **один раз** при создании. `Username` хранится как plain TEXT (consistency через handler-логику, без жёсткого SQL FK).

**Multi-token per user — by design.** UNIQUE стоит на `TokenHash`, не на `(Username, ...)`. Один владелец держит отдельные токены под отдельные скрипты / машины (`Description`: "laptop dev", "ci-deploy", "nightly-backup") — компрометация одной машины revoke'ается без затрагивания остальных; rotation без downtime — новый токен → деплой → revoke старого как штатный flow. Audit actor `<Username>:admin-token:<TokenPrefix>` различает действия разных токенов того же user'а. Лимита на N токенов per user в MVP нет.

**Lifecycle:**
- Self-revoke через `/Admin/Profile` → `IsDeleted=1`, row остаётся для audit-history.
- `IUserAdmin.Delete(username)` → **hard-delete** all tokens одной транзакцией с hard-delete user'а. Reasoning: Users hard-delete'ятся (нет `IsDeleted` колонки); token, переживший user'а, реактивирован быть не может (нет на что вернуть привязку), а audit-log хранит actor строкой `<Username>:admin-token:<TokenPrefix>` — текст переживает удаление row. UI user-delete confirm-dialog обязан показать "user has N active tokens" перед удалением.

В MVP права у токена симметричны user'у — full CRUD на bindings / api-keys. Per-token scope (TagSet-restricted tokens) — deferred вместе с RBAC (см. open questions в `plan.md`).

### AuditLog

Unchanged в сравнении с v1 spec — append-only history всех Bindings/ApiKeys операций. Одна row на каждое write-action.

```sql
CREATE TABLE AuditLog (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    At           INTEGER NOT NULL,
    Actor        TEXT    NOT NULL,
    Action       TEXT    NOT NULL,           -- "Created" | "Updated" | "Deleted" | "Restored"
    EntityType   TEXT    NOT NULL,           -- "Binding" | "ApiKey" | "User"
    TagSetJson   TEXT    NULL,               -- binding's tag-set at time of change
    KeyPath      TEXT    NULL,
    OldValue     TEXT    NULL,               -- for Binding: ValuePlain or b64-bundle if Secret
    NewValue     TEXT    NULL,
    OldHash      TEXT    NULL,
    NewHash      TEXT    NULL
);
```

Index: `(At DESC)` + `(TagSetJson, At DESC)` для per-scope-timeline query.

## 4. Resolve pipeline

### Request

```
GET /v1/conf?env=prod&project=yobapub&region=eu-west&instance=10.0.0.5
Header: X-YobaConf-ApiKey: <token>
Optional: If-None-Match: "<etag>"
```

Query-params = tag-vector. Каждый `key=value` trusted как-есть (всё ещё slug-валидированный). Duplicate keys — последний wins (стандартный HTTP parsing).

### Pipeline stages

1. **Auth.** Ключ валиден; `request.TagVector ⊇ apiKey.RequiredTags` иначе 403.
2. **Candidate lookup.** `SELECT * FROM Bindings WHERE IsDeleted=0 AND TagSet ⊆ request.TagVector`. SQLite реализация: `json_each(TagSetJson)` развертка + `NOT EXISTS (mismatch)` predicate. Если `apiKey.AllowedKeyPrefixes` non-null — фильтр по `KeyPath LIKE prefix%`.
3. **Group by KeyPath.** Для каждого уникального `KeyPath`:
    - Сортировка matched bindings по `TagCount DESC` (максимальная specificity первой).
    - Top-most: `TagCount == top.TagCount` всех remaining → **all tied at max specificity**.
    - Если tied > 1 **и** values различаются → **409 Conflict**:
      ```json
      {
        "error": "conflict",
        "key": "log-level",
        "tiedBindings": [
          {"id": 42, "tagSet": {"env":"prod","project":"yobapub"}, "value": "Info"},
          {"id": 73, "tagSet": {"project":"yobapub","role":"worker"}, "value": "Trace"}
        ],
        "hint": "Add a more-specific overlay with both tag-sets combined to disambiguate."
      }
      ```
    - Если tied > 1 но values идентичны → ok, pick any (deterministic: lowest Id).
    - Иначе — unique winner.
4. **Decrypt secrets.** Binding'и с `Kind=Secret` → `ISecretEncryptor.Decrypt(ciphertext, iv, authTag, keyVersion)`. Plaintext используется для merge, encrypted blob никогда не покидает server.
5. **Expand dotted keys.** `db.host=x`, `db.port=5432` → `{"db":{"host":"x","port":5432}}`. Ordinal-sorted keys на каждом уровне для determinism.
6. **Canonical JSON.** `System.Text.Json` с сорт + compact. ETag = sha256-16-hex.
7. **ETag compare.** If-None-Match match → 304, no body.
8. **Response.** 200 + JSON + ETag header. Secrets в response — plaintext (consumer их хочет).

### Примеры

Request: `?env=prod&project=yobapub&region=eu-west`

Bindings (IsDeleted=0):

| Id | TagSet                              | Key        | Value       | Kind   |
|----|-------------------------------------|------------|-------------|--------|
| 1  | `{}`                                | log-format | `"json"`    | Plain  |
| 2  | `{env: prod}`                       | db-cluster | `"prod-rds"`| Plain  |
| 3  | `{env: prod}`                       | log-level  | `"Info"`    | Plain  |
| 4  | `{project: yobapub}`                | cache.ttl  | `300`       | Plain  |
| 5  | `{env: prod, project: yobapub}`     | db.host    | `"prod-db"` | Plain  |
| 6  | `{env: prod, project: yobapub}`     | db.password| ⟨ciphertext⟩| Secret |

All 6 matched (все TagSet ⊆ request.TagVector). Specificity buckets:
- 0 tags: #1
- 1 tag: #2, #3, #4
- 2 tags: #5, #6

Per-key:
- `log-format` — only #1, wins.
- `db-cluster` — only #2, wins.
- `log-level` — only #3, wins.
- `cache.ttl` — only #4, wins.
- `db.host` — only #5, wins.
- `db.password` — only #6, decrypt then wins.

Response:
```json
{
  "cache": {"ttl": 300},
  "db": {"host": "prod-db", "password": "<decrypted>"},
  "db-cluster": "prod-rds",
  "log-format": "json",
  "log-level": "Info"
}
```

### Пример конфликта

Bindings:
- #10 `{env: prod}` → `log-level=Info`
- #11 `{project: yobapub, role: worker}` → `log-level=Trace`

Request: `?env=prod&project=yobapub&role=worker`

Оба matched. `TagCount` — 1 vs 2. #11 specificity выше → wins. **Не конфликт.**

Другой кейс:
- #20 `{env: prod, project: yobapub}` → `log-level=Info`
- #21 `{project: yobapub, role: worker}` → `log-level=Trace`

Request: `?env=prod&project=yobapub&role=worker`

Оба matched. `TagCount=2` одинаковый. TagSets incomparable (ни один не subset). **409 Conflict.**

Admin создаёт overlay `{env:prod, project:yobapub, role:worker}` → `log-level=Trace` → specificity 3, wins. Конфликт разрешён.

## 5. UI (админка)

### Dashboard
Vertical table of Bindings, сверху — facet-filter bar. Без явной tag-vocabulary (deferred, см. §14) tag-key dropdown'ы строятся из fact-values: `SELECT DISTINCT json_extract(...)` по `Bindings.TagSetJson` — actually-used keys и их values.

```
[ env: any ▾ ]  [ project: any ▾ ]  [ region: any ▾ ]  [ + add facet ]
                                                                [ + New binding ]
```

Активные фильтры сужают table rows (`WHERE matching tag-value AND/OR no-tag-set`). Table columns:

| TagSet (chips) | Key | Value | Updated | Actions |
|---|---|---|---|---|
| `env=prod, project=yobapub` | `db.host` | `prod-db.internal` | 2d ago | ⋯ |
| `env=prod` | `log-level` | `Info` | 5d ago | ⋯ |
| ... | | | | |

Row click → detail/edit view. Secrets в value column маскированы `••••••` по-default, reveal через 👁 (single-read, audit-logged).

### Binding editor (Create / Edit)

Panel-модал:
- Tags: multi-field — для каждого tag-key input + value input. Autocomplete — по `DISTINCT` tag-values уже в базе; свободный ввод для нового tag-key / value.
- Key: input, dotted form.
- Value: textarea (Plain) или password-input с reveal-button (Secret).
- Kind: radio Plain | Secret.
- Live preview: "With this binding, a resolve for `{env:prod}` would include: ...". Покажет если binding полностью перекрыт other bindings.
- Conflict check: "This binding is incomparable with binding #X on key=Y. Resolves matching both tags will 409. Add overlay `{combined-tags}` to resolve."

### History / Rollback

Same semantics как v1 spec §7:
- `/History` timeline с filters (by tag-value, entity-type, actor, key-path).
- Per-entry Rollback → new Upsert с actor=`restore:<id>`.
- No 3-way merge на первой итерации.

### /admin/users

CRUD для Users table. Create → username + password → PBKDF2-hash → row. Rotate password — отдельный action (open editor → new password → update hash, audit row). Delete через confirm-dialog. Запрет на удаление последнего user'а (иначе config-fallback просыпается, что обычно не intended). Паттерн один-в-один с yobalog'ом.

### /admin/api-keys

CRUD для ApiKeys table. Create-form:
- Description (required)
- Required tags (tag-picker, minimum 1 recommended для security)
- Allowed key prefixes (textarea, empty = no restriction)
- Generated plaintext shown **один раз** (on create), hashed stored. Display prefix (6 chars) для идентификации в UI.
- Soft-delete через confirm.

Паттерн заимствован из yobalog (`SqliteApiKeyStore` + `IApiKeyAdmin`): `ShortGuid` 22-char token, sha256(token)+prefix-index для lookup'ов.

## 6. Масштабируемость и отказоустойчивость

- Pet-scale. Max ~200 bindings expected, resolve queries O(matched bindings × log N) — well under 10ms.
- Single SQLite file, WAL mode. No horizontal scaling in MVP.
- Horizontal read-replicas когда-нибудь: read-only SQLite replicas via Litestream или similar. Not in MVP.
- Cache-friendly: ETag + If-None-Match. Consumer polls → 304 на no-change.

## 7. Версионирование и аудит

См. §3 AuditLog schema.

- Every Bindings/ApiKeys/TagVocabulary write → append entry.
- `Actor` = cookie-auth admin OR `restore:<id>` OR `system` (bootstrap).
- Rollback per-entry: reads entry, issues inverse Upsert, generates fresh audit row (rollback itself is auditable).
- No direct writes to AuditLog; only storage impl populates.

## 8. API + auth

### Public surface

- `GET /v1/conf?...` — main resolve. Auth via `X-YobaConf-ApiKey` header or `?apiKey=` query.
- `GET /health` — anonymous, 200 on process-up.
- `GET /ready` — anonymous, 200 if SQLite reachable.
- `GET /version` — anonymous, build provenance.

### Admin surface

Razor-Pages + htmx — primary UI (cookie-auth, antiforgery). JSON-endpoints под `/v1/admin/*` за **personal admin tokens** (см. §3 AdminTokens) для scripting / automation:

- Auth — любой из:
    - `Authorization: Bearer <token>` (primary, HTTP-стандарт),
    - `X-YobaConf-AdminToken: <token>` (equivalent, симметрично `X-YobaConf-ApiKey`),
    - `?adminToken=<token>` query (fallback для curl-quick-test).

    Header'ы > query при наличии. Если `Authorization: Bearer` и `X-YobaConf-AdminToken` присутствуют с **разными** значениями — 400 `ambiguous_auth` (refuse to guess). Constant-time hash compare. 401 JSON на missing / invalid / soft-deleted / orphan-username.
- `PUT /v1/admin/bindings` body `{tagSet, keyPath, kind, value}` — upsert (idempotent на (TagSet, KeyPath)).
- `DELETE /v1/admin/bindings/{id}` — soft-delete.
- `GET /v1/admin/bindings?tag={k}={v}&key={pattern}` — list (secrets — `value=null` + `kind=Secret`, plaintext не leak'ает).
- `PUT /v1/admin/api-keys` / `DELETE /v1/admin/api-keys/{id}` / `GET /v1/admin/api-keys` — runtime-keys CRUD. Plaintext token возвращается один раз при создании.
- `POST /v1/admin/rollback/{auditId}` — rollback (deferred к Phase D wiring; admin-API становится JSON-обёрткой над `IBindingStoreAdmin.Restore`).

Token-auth выбран вместо cookie-reuse: cookie-флоу требует POST `/Login` + парсинг `Set-Cookie` + antiforgery-token в скрипте; admin-token — один header, отзыв через `/Admin/Profile` без смены пароля. Lifecycle токенов независим от UI-сессий.

Audit actor для admin-token-инициированных изменений — `<Username>:admin-token:<TokenPrefix>`, для UI-cookie — `<Username>`, для runtime-key (только resolve, не пишет) — N/A. См. §7.

### API-key scope

Validated on every resolve:
1. `request.TagVector ⊇ apiKey.RequiredTags` (exact-match subset).
2. If `apiKey.AllowedKeyPrefixes` — filter response to bindings whose `KeyPath` has one of those prefixes.

Typical keys:
- `{env: prod}` + no key restriction = prod ops/runtime key.
- `{env: prod, project: yobapub}` + no key restriction = yobapub prod runtime.
- `{env: prod}` + `db.*`, `cache.*` prefixes = specific-access reader for monitoring/observability tooling.

## 9. Consumer runtime integration

Основная модель потребления — **env-vars через sidecar runner**, а не SDK-in-app. Все сервисы владельца поднимаются в Docker; общий entrypoint-scheme превращает resolved JSON в exported env vars, затем exec'ит целевой процесс.

### 9.1 Alias templates

Один binding key доступен потребителю под несколькими именами, согласно runtime-template'у. Основные:

| Template | Mapping from `db.host` | Commentary |
|---|---|---|
| `dotnet` | `db__host` | `Microsoft.Extensions.Configuration` разбивает env на sections по `__` |
| `envvar` | `DB_HOST` | POSIX-style: dots → underscore, uppercase |
| `envvar_deep` | `DB__HOST` | uppercase + double-underscore (Kubernetes / Helm convention) |
| `flat` | `db.host` | literal key, для Spring-style consumer'ов |

Template выбирается consumer'ом на request-time: `GET /v1/conf?env=prod&template=dotnet` → response shape возвращает `db__host=…` pairs вместо nested JSON. Template=`flat` (default если не указан) сохраняет legacy nested-JSON response из §4.

Server-side: template применяется **после** expand-dotted-step в pipeline. Alias-per-binding (individual override на отдельный key, напр. `AWS_ACCESS_KEY_ID` — literal name независимо от template'а) хранится как optional `Aliases` column в Bindings (JSON-dict `{templateName: aliasName}`); fallback на template-derivation если запись пустая.

### 9.2 `yobaconf-run` CLI

Self-contained .NET single-file binary. Fetch → export → exec. Basic flow:

```bash
yobaconf-run \
  --endpoint https://yobaconf.3po.su \
  --api-key "$YOBACONF_API_KEY" \
  --tag env=prod \
  --tag project=yobapub \
  --tag host=$(hostname) \
  --template envvar_deep \
  -- dotnet MyApp.dll
```

Steps:
1. HTTP GET к `/v1/conf?env=prod&project=yobapub&host=…&template=envvar_deep` с `X-YobaConf-ApiKey` header.
2. Parse response (200 → plain mapping; 409 → print diagnostic + exit 2; 403 → print "api-key scope mismatch" + exit 3).
3. Apply each pair to current process environment via `Environment.SetEnvironmentVariable`.
4. `execve` (Unix) / `CreateProcess` with inherit-env (Windows) на child-args после `--`.

Child inherits the patched env block. Signal forwarding (SIGTERM / SIGINT) — proxy'ит в child, ждёт child exit, возвращает его exit-code.

Docker integration:

```dockerfile
COPY --from=yobaconf /yobaconf-run /usr/local/bin/yobaconf-run
ENTRYPOINT ["yobaconf-run", \
  "--endpoint=https://yobaconf.3po.su", \
  "--template=dotnet", \
  "--", "dotnet", "MyApp.dll"]
```

`--api-key` и `--tag host=…` передаются через `docker run -e` / compose переменные. Runner читает `YOBACONF_*` env-vars при отсутствии CLI-флагов (env > default, flag > env).

### 9.3 SDK (optional, secondary)

`.NET` SDK (`YobaConf.Client`) остаётся для use-case'ов, где env-экспорт не подходит — hot-reload конфига внутри long-running process'а без restart'а, или ETag polling с custom response-handling. API переписывается на tagged-tags (`AddYobaConf(opts => opts.WithTags(new { env = "prod" }))`), но runner покрывает 90% случаев — SDK не блокирует MVP-release.

## 10. Фронтенд и UI-технологии

Unchanged: Tailwind + DaisyUI (dark theme, kustomization forbidden), TypeScript + bun. Никаких CodeMirror.

## 11. Сборка фронта

Unchanged — `package.json` + bun + Tailwind.

## 12. Развёртывание и HTTPS

Unchanged:
- Chiseled Docker (`mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled`)
- Caddy HTTPS terminator на shared-host, port 8081 для yobaconf
- GitHub Actions CI: merged PR job, NuGet + Playwright caches, Docker BuildX с GHA layer cache.

## 13. Self-observability

Unchanged:
- Logs через `Seq.Extensions.Logging` → yobalog Seq-compat (см. `doc/logging-policy.md`).
- Traces через OTLP → yobalog `/v1/traces`. ActivitySources `YobaConf.Resolve` + `YobaConf.Storage.Sqlite`.
- ETag-compute / JSON-serialize / decrypt спаны mapped к новым стадиям resolve pipeline'а.

## 14. Локализация

Unchanged. English-only MVP. `IStringLocalizer` каркас для последующего перевода. CI non-ASCII check на user-facing файлы.

## 15. Deferred (post-MVP)

Намеренно вынесено за MVP-scope — не блокирует dog-food'ability, включается по мере реального спроса.

### 15.1 Paste-import (JSON / YAML / `.env`)

Paste-форма на `/Import`: textarea + format dropdown → parse → flatten в dotted-key leaves → классификация per-leaf (Plain / Secret) → target tag-picker → N bindings с общим tag-set'ом. Parser'ы — `System.Text.Json` для JSON, `YamlDotNet` для YAML, самописный `.env` (converter'ы уже были реализованы в v1 Phase B.7; из старой ветки переиспользуются). Ценность — ускоряет onboarding существующих конфигов; до появления таковых inline-add-row в `/Bindings` закрывает use-case.

### 15.2 Tag vocabulary + `/Tags` page

Optional schema: `TagVocabulary(TagKey, AllowedValues, IsRequired, Description)`. UI на `/Tags` — CRUD. Warnings в редакторе binding'а ("unknown tag — typo?"). До прорастания реальной проблемы typo-catching не окупается — free-form tag-keys работают корректно, dashboard dropdown'ы строятся из actually-used values через `DISTINCT`.

### 15.3 Tag-priority escape hatch

`priority` column на TagVocabulary (или отдельная `TagPriorities` таблица) → tie-breaker при incomparable conflict'е. Scoped за feature-flag, fail-fast 409 остаётся primary behavior. Делаем если конфликты станут частыми в реальной работе — не premature.

### 15.4 Master-key rotation

CLI-команда `--rotate-master-key <old> <new>`: перечитать все `Kind=Secret` bindings, decrypt old → encrypt new → `KeyVersion` bump + audit entry на каждую row. Не блокирует MVP — первая ротация понадобится через годы.

### 15.5 Secret reveal single-read

POST-endpoint с server-cached 10s-window вместо client-side reveal через data-attribute. Audit entry per reveal. Upgrade UX safety при multi-admin access к секретам.

### 15.6 Python + TypeScript SDKs

Native implementations для cases где runner не подходит (long-running app с hot-reload конфигов). .NET SDK переписывается в Phase C (основной). Python / TS — по факту появления consumer'а.

### 15.7 Horizontal scaling

Litestream → read-only SQLite replicas; push-intergрation в Redis/Consul/S3. Pet-scale (200 bindings, единичные req/s) далёк от bottleneck'а.

### 15.8 Perf benchmark

Soft goal "p99 < 50ms resolve на 200 bindings × 5-dim tag-vector" — BDN suite когда появится нагрузочная необходимость.

---

## Миграция из v1 (path-tree)

Нет production-data для миграции (v1 stack был развернут, но реальные consumer'ы не подключены). План:

- **Data:** dev/prod SQLite файлы (`yobaconf.db`) удаляются на следующем redeploy. Новая схема создаётся с нуля (CREATE IF NOT EXISTS — идемпотентно).
- **Spec/Plan:** этот файл заменяет v1 spec. v1 plan переписывается в новые фазы (см. `plan.md`).
- **Code:** Phase A+B path-tree код остаётся в git history до completion нового Phase A. После — старые файлы удаляются (Nodes/Variables/Secrets/ResolvePipeline/NodePath/etc.). Сохраняются: `AesGcmSecretEncryptor`, `AdminPasswordHasher`, `ActivitySources`, `YobaConfApp.ConfigureServices` (adapted), deployment/CI infra.
- **Consumer SDKs:** `YobaConf.Client` (.NET) переписывается — path → query-params + tag-vector. Future Python/JS clients — native tagged from-scratch.
- **decision-log:** v1 decisions по path/HOCON/include остаются (historical), plus новая запись "Pivot to tagged model" с полным rationale.

## Основные инварианты

- **HOCON выкинут целиком.** Ни в storage, ни в import, ни в SDK. Paste-import (JSON/YAML/.env) — deferred (§15.1).
- **Никаких path'ов.** TagSet — primary addressing.
- **Резолв детерминирован.** Incomparable tie на одном key → 409, никакого silent выбора.
- **TagSet canonical JSON — byte-identical для identical content.** Зависит от ordinal sort of keys.
- **Dotted keys expand в nested JSON** на `template=flat` response; на `template=dotnet`/`envvar`/`envvar_deep` — flat key=value mapping согласно §9.1.
- **Secrets encrypted at rest always.** Plaintext — только в resolve response и в transient admin-reveal.
- **Audit append-only.** Rollback — новый Upsert, не edit прошлой row.
- **Multi-admin, права симметричны.** Любой user в Users table имеет полный CRUD — role-split'ы deferred.
- **Consumer runtime = runner-первый.** `yobaconf-run` покрывает Docker-запуск use-case; SDK — secondary (для hot-reload внутри процесса).
