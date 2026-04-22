# YobaConf — Specification (tagged model, v2)

Центральный self-hosted сервис конфигурации для small-scale стека сервисов одного владельца (5-10 consumer'ов). Admin пишет значения через web-UI, runtime-сервисы тянут resolved JSON по HTTP. **Модель — tagged bindings**: конфиг хранится как плоские `(tag-set, key, value)` triple'ы, resolve — subset-merge с детерминированной обработкой конфликтов.

Этот spec — вторая версия. v1 был path-tree с HOCON-as-storage; отказались 2026-04-21 из-за реального multi-dimensional use-case'а + отпавшей необходимости в HOCON (см. `decision-log.md` "Pivot to tagged model"). Phase A+B path-tree-код живёт в git-history до момента реализации v2 — как ref-имплементация для audit/encryption/tracing/CI (которые переносятся как есть).

## 1. Архитектурные принципы

- **Single-admin, pet-scale.** 5-10 consumer'ов, ≤200 bindings, ≤5 measurement-осей. Не multi-tenant SaaS.
- **Tagged bindings, не hierarchy.** Конфиг — плоская таблица `(tag-set, key, value)`. Никаких иерархических путей, никакого "дерева нод".
- **Subset-merge resolve.** Consumer шлёт tag-vector; сервер находит все bindings с `TagSet ⊆ request.TagVector`, мержит по specificity (больше tags = выше приоритет), возвращает canonical JSON.
- **Fail-fast на incomparable конфликтах.** Если две incomparable (ни одно не subset другого) bindings определяют одинаковый key и обе match'ят request — resolve возвращает 409 Conflict с явным diagnostic'ом. Admin добавляет more-specific overlay для disambiguation. Priority-based escape hatch — Phase D+, за interface'ом.
- **HOCON выкинут.** Storage — flat triple'ы, paste-import — JSON / YAML / `.env` flatten'ится в dotted-key triple'ы. Никакого HOCON parser'а ни на чтении, ни на записи.
- **Секреты — kind'ом binding'а.** Не отдельная сущность; AES-256-GCM encrypted ciphertext в тех же Bindings-rows с `Kind=Secret`. Resolve decrypt'ит автоматически.
- **Immutable audit.** Каждая запись — строка в AuditLog с old/new value + hash, append-only. Rollback = upsert из previous-value.
- **yobaconf → yobalog однонаправленно.** yobaconf шлёт свои события в yobalog через CLEF endpoint. Обратное запрещено (bootstrap cycle).

## 2. Технологический стек

- **Платформа:** .NET 10 (монолит, Razor Pages + htmx).
- **База данных:** SQLite через `linq2db.SQLite.MS` (синхронно с yobalog'овским стеком — единый tooling). Single-file `.db`, WAL mode.
- **Tag-сет хранение:** canonical-JSON TEXT column + `json_each`/`json_extract` из SQLite JSON1 для subset-match запросов. Индекс на `(KeyName, TagSet_canonical)`.
- **Paste-import parsers:** `System.Text.Json` для JSON, `YamlDotNet` для YAML, самописный `.env` parser (как в v1 Phase B.7). Каждый flatten'ится в dotted-key leaf-triple'ы.
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

### TagVocabulary (optional schema)

Admin может объявить известные tag-keys + allowed-values. Без явной схемы — free-form (любые tag-keys принимаются).

```sql
CREATE TABLE TagVocabulary (
    TagKey         TEXT    PRIMARY KEY,
    AllowedValues  TEXT    NULL,     -- JSON array ["prod","staging"] или NULL для any
    IsRequired     INTEGER NOT NULL,  -- 0/1: must appear in every binding
    Description    TEXT    NULL,
    CreatedAt      INTEGER NOT NULL
);
```

**Поведение:**
- Если `TagKey` есть в vocabulary с непустым `AllowedValues` → binding с этим tag'ом обязан иметь value ∈ AllowedValues.
- Если `IsRequired=1` → каждый binding должен содержать этот tag.
- Free-form (не в vocabulary) tag'и принимаются, UI показывает warning "unknown tag — typo?".

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

### AuditLog

Unchanged в сравнении с v1 spec — append-only history всех Bindings/ApiKeys операций. Одна row на каждое write-action.

```sql
CREATE TABLE AuditLog (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    At           INTEGER NOT NULL,
    Actor        TEXT    NOT NULL,
    Action       TEXT    NOT NULL,           -- "Created" | "Updated" | "Deleted" | "Restored"
    EntityType   TEXT    NOT NULL,           -- "Binding" | "ApiKey" | "TagVocabulary"
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
        "key": "logLevel",
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
| 1  | `{}`                                | log_format | `"json"`    | Plain  |
| 2  | `{env: prod}`                       | db_cluster | `"prod-rds"`| Plain  |
| 3  | `{env: prod}`                       | log_level  | `"Info"`    | Plain  |
| 4  | `{project: yobapub}`                | cache.ttl  | `300`       | Plain  |
| 5  | `{env: prod, project: yobapub}`     | db.host    | `"prod-db"` | Plain  |
| 6  | `{env: prod, project: yobapub}`     | db.password| ⟨ciphertext⟩| Secret |

All 6 matched (все TagSet ⊆ request.TagVector). Specificity buckets:
- 0 tags: #1
- 1 tag: #2, #3, #4
- 2 tags: #5, #6

Per-key:
- `log_format` — only #1, wins.
- `db_cluster` — only #2, wins.
- `log_level` — only #3, wins.
- `cache.ttl` — only #4, wins.
- `db.host` — only #5, wins.
- `db.password` — only #6, decrypt then wins.

Response:
```json
{
  "cache": {"ttl": 300},
  "db": {"host": "prod-db", "password": "<decrypted>"},
  "db_cluster": "prod-rds",
  "log_format": "json",
  "log_level": "Info"
}
```

### Пример конфликта

Bindings:
- #10 `{env: prod}` → `log_level=Info`
- #11 `{project: yobapub, role: worker}` → `log_level=Trace`

Request: `?env=prod&project=yobapub&role=worker`

Оба matched. `TagCount` — 1 vs 2. #11 specificity выше → wins. **Не конфликт.**

Другой кейс:
- #20 `{env: prod, project: yobapub}` → `log_level=Info`
- #21 `{project: yobapub, role: worker}` → `log_level=Trace`

Request: `?env=prod&project=yobapub&role=worker`

Оба matched. `TagCount=2` одинаковый. TagSets incomparable (ни один не subset). **409 Conflict.**

Admin создаёт overlay `{env:prod, project:yobapub, role:worker}` → `log_level=Trace` → specificity 3, wins. Конфликт разрешён.

## 5. UI (админка)

### Dashboard
Vertical table of Bindings, сверху — facet-filter bar:

```
[ env: any ▾ ]  [ project: any ▾ ]  [ region: any ▾ ]  [ + add facet ]
                                                                [ + New binding ]
```

Активные фильтры сужают table rows (`WHERE matching tag-value AND/OR no-tag-set`). Table columns:

| TagSet (chips) | Key | Value | Updated | Actions |
|---|---|---|---|---|
| `env=prod, project=yobapub` | `db.host` | `prod-db.internal` | 2d ago | ⋯ |
| `env=prod` | `log_level` | `Info` | 5d ago | ⋯ |
| ... | | | | |

Row click → detail/edit view. Secrets в value column маскированы `••••••` по-default, reveal через 👁 (single-read, audit-logged).

### Binding editor (Create / Edit)

Panel-модал:
- Tags: multi-field — для каждого known tag-key выбор из dropdown (values из TagVocabulary) + возможность добавить новый tag-key.
- Key: input, dotted form.
- Value: textarea (Plain) или password-input с reveal-button (Secret).
- Kind: radio Plain | Secret.
- Live preview: "With this binding, a resolve for `{env:prod}` would include: ...". Покажет если binding полностью перекрыт other bindings.
- Conflict check: "This binding is incomparable with binding #X on key=Y. Resolves matching both tags will 409. Add overlay `{combined-tags}` to resolve."

### Tag vocabulary page

`/Tags`: CRUD для TagVocabulary. Для каждого tag-key — value-list, required-flag, description. Warning-banner если есть bindings с unknown tag-keys (potential typos).

### History / Rollback

Same semantics как v1 spec §7:
- `/History` timeline с filters (by tag-value, entity-type, actor, key-path).
- Per-entry Rollback → new Upsert с actor=`restore:<id>`.
- No 3-way merge на первой итерации.

### Import

Paste JSON / YAML / `.env` → parse → flatten в dotted-key leaf-list → **classify**: per-leaf Plain / Secret (как в v1 Phase B.7, adapted — variable/secret теперь просто Kind на binding'е, "keep" больше не нужен т.к. storage = leaf-triple'ы).

Target — не path а **target tag-set**. Import UI шаг 1: выбор target tag-set (tag-picker). Шаг 2: preview-classify table (каждый leaf — key + value + radio Plain/Secret). Save создаёт N bindings с тем же target tag-set, по одному на leaf.

### API Keys page

`/ApiKeys`: CRUD. Create-form:
- Description (required)
- Required tags (tag-picker, minimum 1 recommended для security)
- Allowed key prefixes (textarea, empty = no restriction)
- Generated plaintext shown once, hashed stored.

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

Currently Razor-Pages + htmx (не JSON API). Future: JSON-endpoints за `[Authorize]` cookie-auth for scripting/automation:
- `PUT /v1/admin/bindings` body `{tagSet, key, value, kind}` — upsert.
- `DELETE /v1/admin/bindings/{id}` — soft-delete.
- `GET /v1/admin/bindings?tag={k}={v}&key={pattern}` — list.
- `POST /v1/admin/rollback/{auditId}` — rollback.

### API-key scope

Validated on every resolve:
1. `request.TagVector ⊇ apiKey.RequiredTags` (exact-match subset).
2. If `apiKey.AllowedKeyPrefixes` — filter response to bindings whose `KeyPath` has one of those prefixes.

Typical keys:
- `{env: prod}` + no key restriction = prod ops/runtime key.
- `{env: prod, project: yobapub}` + no key restriction = yobapub prod runtime.
- `{env: prod}` + `db.*`, `cache.*` prefixes = specific-access reader for monitoring/observability tooling.

## 9. Фронтенд и UI-технологии

Unchanged: Tailwind + DaisyUI (dark theme, kustomization forbidden), TypeScript + bun. Prism.js для syntax-highlighting в paste-import preview (JSON / YAML). Simple inline inputs в binding editor + textarea в paste-import — никаких CodeMirror и подсветки HOCON (HOCON выпилен).

## 10. Сборка фронта

Unchanged — `package.json` + bun + Tailwind.

## 11. Развёртывание и HTTPS

Unchanged:
- Chiseled Docker (`mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled`)
- Caddy HTTPS terminator на shared-host, port 8081 для yobaconf
- GitHub Actions CI: merged PR job, NuGet + Playwright caches, Docker BuildX с GHA layer cache.

## 12. Self-observability

Unchanged:
- Logs через `Seq.Extensions.Logging` → yobalog Seq-compat (см. `doc/logging-policy.md`).
- Traces через OTLP → yobalog `/v1/traces`. ActivitySources `YobaConf.Resolve` + `YobaConf.Storage.Sqlite`.
- ETag-compute / JSON-serialize / decrypt спаны mapped к новым стадиям resolve pipeline'а.

## 13. Локализация

Unchanged. English-only MVP. `IStringLocalizer` каркас для последующего перевода. CI non-ASCII check на user-facing файлы.

---

## Миграция из v1 (path-tree)

Нет production-data для миграции (v1 stack был развернут, но реальные consumer'ы не подключены). План:

- **Data:** dev/prod SQLite файлы (`yobaconf.db`) удаляются на следующем redeploy. Новая схема создаётся с нуля (CREATE IF NOT EXISTS — идемпотентно).
- **Spec/Plan:** этот файл заменяет v1 spec. v1 plan переписывается в новые фазы (см. `plan.md`).
- **Code:** Phase A+B path-tree код остаётся в git history до completion нового Phase A. После — старые файлы удаляются (Nodes/Variables/Secrets/ResolvePipeline/NodePath/etc.). Сохраняются: `AesGcmSecretEncryptor`, `AdminPasswordHasher`, `ActivitySources`, `YobaConfApp.ConfigureServices` (adapted), deployment/CI infra.
- **Consumer SDKs:** `YobaConf.Client` (.NET) переписывается — path → query-params + tag-vector. Future Python/JS clients — native tagged from-scratch.
- **decision-log:** v1 decisions по path/HOCON/include остаются (historical), plus новая запись "Pivot to tagged model" с полным rationale.

## Основные инварианты

- **HOCON выкинут целиком.** Ни в storage, ни в import. Paste-import — только JSON / YAML / `.env`.
- **Никаких path'ов.** TagSet — primary addressing.
- **Резолв детерминирован.** Incomparable tie на одном key → 409, никакого silent выбора.
- **TagSet canonical JSON — byte-identical для identical content.** Зависит от ordinal sort of keys.
- **Dotted keys expand в nested JSON** на response. Storage — flat.
- **Secrets encrypted at rest always.** Plaintext — только в resolve response и в transient admin-reveal.
- **Audit append-only.** Rollback — новый Upsert, не edit прошлой row.
