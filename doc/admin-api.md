# YobaConf admin API (`/v1/admin/*`)

JSON endpoints for scripting / automation against the YobaConf admin surface. Mirrors the
operations available through the Razor admin UI (`/Bindings`, `/admin/api-keys`) so
scripts can do bulk-set, cross-environment updates, and runtime-key rotation without
clicking through the UI.

Spec: `doc/spec.md` §8 "Admin surface". Decision rationale: `doc/decision-log.md`
2026-04-26 "Admin API: personal admin tokens".

## Auth — personal admin tokens

Every `/v1/admin/*` request carries a personal admin token. Tokens are owned by a User
(per-user; multi-token by design — separate token per machine / script). Self-service
CRUD lives at `/Admin/Profile` in the UI.

Three equivalent transports:

| Transport | Header / param | Notes |
|---|---|---|
| `Authorization` | `Authorization: Bearer <token>` | HTTP standard; primary. |
| Custom header | `X-YobaConf-AdminToken: <token>` | Mirrors `X-YobaConf-ApiKey`. |
| Query | `?adminToken=<token>` | Fallback for curl-quick-test. |

If both `Authorization: Bearer` **and** `X-YobaConf-AdminToken` arrive with **different
values**, the request is rejected with `400 ambiguous_auth` — the server refuses to guess
which one was intended. Same value in both is accepted.

```bash
# Choose whichever your tooling prefers. The examples below use Bearer.
export YOBACONF_ENDPOINT="https://yobaconf.example.com"
export YOBACONF_ADMIN_TOKEN="<22-char token from /Admin/Profile>"
alias yc="curl -sSf -H 'Authorization: Bearer '$YOBACONF_ADMIN_TOKEN -H 'Content-Type: application/json'"
```

## Endpoints

### `PUT /v1/admin/bindings` — upsert a binding

Idempotent on `(tagSet, keyPath)`. Request body:

```json
{
  "tagSet": {"env": "prod", "project": "yobapub"},
  "keyPath": "db.host",
  "kind": "Plain",
  "value": "prod-db.internal"
}
```

- `tagSet` — JSON object of `{tagKey: "tagValue"}`. Both keys and values must match the
  slug regex `[a-z][a-z0-9-]{0,39}` (optional leading `$` for system tags).
- `keyPath` — dotted form (`db.host`, `cache.policy.lru`); each segment is a slug.
- `kind` — `"Plain"` or `"Secret"`.
- `value` — for `Plain`, any JSON value (`"text"`, `42`, `true`, `null`); for `Secret`,
  a JSON string with the plaintext (server encrypts with AES-256-GCM, plaintext never
  hits storage).

Response (201 on create, 200 on update):

```json
{"id": 42, "etag": "<sha256-of-content>", "created": true}
```

### `DELETE /v1/admin/bindings/{id}` — soft-delete

`204` on success, `404` if the binding doesn't exist or is already deleted.

### `GET /v1/admin/bindings` — list with filters

Query params (all optional, AND together):

- `?tag=key=value` — repeat to require multiple tags. Binding must contain every
  `(key, value)` exactly. Example: `?tag=env=prod&tag=project=yobapub`.
- `?key=prefix` — prefix match on `keyPath` (no glob; `db.` matches `db.host` and
  `db.port`, not `cache.db`).

Response is a JSON array. Secrets are redacted server-side: `"value": null` plus
`"kind": "Secret"`. Use the `kind` field to disambiguate from plain `null` values.

```json
[
  {"id": 42, "tagSet": {"env":"prod"}, "keyPath": "db.host", "kind": "Plain", "value": "prod-db", "updatedAt": "2026-04-26T...", "etag": "..."},
  {"id":  7, "tagSet": {"env":"prod"}, "keyPath": "db.password", "kind": "Secret", "value": null, "updatedAt": "...", "etag": "..."}
]
```

### `PUT /v1/admin/api-keys` — create a runtime API key

Request:

```json
{
  "description": "yobapub prod runtime",
  "requiredTags": {"env": "prod", "project": "yobapub"},
  "allowedKeyPrefixes": ["db.", "cache."]
}
```

Response (`201`) returns the **plaintext exactly once** — the only chance to capture it:

```json
{
  "id": 17,
  "prefix": "AbcDeF",
  "plaintext": "AbcDeF...22charsXX",
  "description": "yobapub prod runtime",
  "requiredTags": {"env": "prod", "project": "yobapub"},
  "allowedKeyPrefixes": ["db.", "cache."],
  "updatedAt": "..."
}
```

### `DELETE /v1/admin/api-keys/{id}` — soft-delete

`204` / `404`. Soft-deleted keys reject `Validate` immediately (`401` on `/v1/conf`).

### `GET /v1/admin/api-keys` — list (no plaintexts)

Returns active keys without `plaintext`. Use the `prefix` field for human identification.

## Use cases

### 1. Set 14 values for one tag-set

```bash
TAGS='{"env":"prod","project":"yobapub"}'

declare -A vars=(
  [db.host]='"prod-db.internal"'
  [db.port]=5432
  [db.user]='"yobapub"'
  [cache.host]='"cache.internal"'
  [cache.ttl]=300
  [log-level]='"Info"'
  [log-format]='"json"'
  [feature.dark-mode]=true
  [feature.beta]=false
  [api.timeout-ms]=15000
  [api.max-retries]=3
  [smtp.host]='"smtp.internal"'
  [smtp.port]=587
  [smtp.from]='"noreply@example.com"'
)

for key in "${!vars[@]}"; do
  yc -X PUT "$YOBACONF_ENDPOINT/v1/admin/bindings" \
    -d "{\"tagSet\":$TAGS,\"keyPath\":\"$key\",\"kind\":\"Plain\",\"value\":${vars[$key]}}"
done
```

### 2. Update one key across three environments

```bash
NEW_HOST='"db-2026-04.internal"'

for env in dev staging prod; do
  yc -X PUT "$YOBACONF_ENDPOINT/v1/admin/bindings" \
    -d "{\"tagSet\":{\"env\":\"$env\"},\"keyPath\":\"db.host\",\"kind\":\"Plain\",\"value\":$NEW_HOST}"
done
```

### 3. Rotate a runtime API key without downtime

```bash
# 1. Issue a new key with the same scope as the old one.
NEW=$(yc -X PUT "$YOBACONF_ENDPOINT/v1/admin/api-keys" -d '{
  "description": "yobapub prod runtime (2026-Q2)",
  "requiredTags": {"env":"prod","project":"yobapub"}
}')
NEW_PLAINTEXT=$(echo "$NEW" | jq -r .plaintext)
NEW_ID=$(echo "$NEW" | jq .id)

# 2. Roll the new plaintext into your runtime config. Wait for consumers to pick it up.
# 3. Soft-delete the old key.
OLD_ID=...   # find via GET /v1/admin/api-keys
yc -X DELETE "$YOBACONF_ENDPOINT/v1/admin/api-keys/$OLD_ID"
```

### 4. List bindings filtered by tag (backup script)

```bash
yc "$YOBACONF_ENDPOINT/v1/admin/bindings?tag=env=prod" \
  | jq '.[] | select(.kind == "Plain") | {keyPath, value}'
```

`Secret` bindings show up with `value: null` — to back up secrets, you need the master
key + a separate decrypt path (out of scope for the admin API; reveal happens only
through the UI single-read flow, see spec §15.5).

## Audit

Every write through `/v1/admin/*` creates an `AuditLog` row with
`Actor = <Username>:admin-token:<TokenPrefix>`. Use the format to filter the history
page or query directly:

- `<Username>:admin-token:<TokenPrefix>` — admin-token-initiated writes.
- `<Username>` — UI-cookie-session writes.
- `apikey:<TokenPrefix>` — runtime-key (currently read-only via `/v1/conf`; reserved
  for future write-capable runtime flows).

## Errors

| Code | When |
|---|---|
| `400 bad_request` | malformed JSON, invalid slug, missing required field. |
| `400 ambiguous_auth` | `Authorization: Bearer` and `X-YobaConf-AdminToken` carry different values. |
| `401 unauthorized` | missing token, unknown token, soft-deleted token. |
| `404 not_found` | DELETE on missing/soft-deleted resource. |
| `503 service_unavailable` | `Kind=Secret` PUT without `YOBACONF_MASTER_KEY` configured. |

## Rate limit / lifecycle

No rate limit in MVP (single-owner pet-scale). Tokens have no automatic expiry —
revoke explicitly through `/Admin/Profile` or by deleting the user (cascade hard-deletes
all of their tokens, decision-log 2026-04-26).
