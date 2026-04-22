# Consumer integration

How to wire one of your own services to pull config from YobaConf at startup. The runtime
model is **env vars through `yobaconf-run`** — a single sidecar binary that fetches
resolved JSON from `/v1/conf`, exports each key/value as a process env var, and exec's
your child command with that env inherited.

This is deliberately simpler than an SDK. A compose-friendly container entrypoint does
all the work; the app reads env vars like it always has.

## 1. Provision an API key

From `/admin/api-keys` in the YobaConf web UI:

- **Description**: a human string (e.g. `yobapub prod runtime`)
- **Required tags**: the tag floor the key must see on every request. For a prod runtime
  token: `env=prod`, `project=yobapub`. For a per-host override token: just
  `env=prod` (host gets filled in at request time).
- **Allowed key prefixes**: optional hard filter on which bindings the key can read.
  Leave blank for full access; fill with `db.`, `cache.` etc. to hand out a
  monitoring-only token.
- Hit **Create** — the plaintext shows once. Save it to your deployment secret store
  (GitHub secrets, Vault, 1Password, etc.). The server keeps only a sha256 hash.

## 2. Pick a response template

Templates drive how key paths become env var names:

| Template       | `db.host` becomes | Use when                                           |
|----------------|-------------------|----------------------------------------------------|
| `envvar`       | `DB_HOST`         | POSIX apps reading `$DB_HOST` directly             |
| `envvar-deep`  | `DB__HOST`        | Helm / Kubernetes convention (double underscore)   |
| `dotnet`       | `db__host`        | `Microsoft.Extensions.Configuration` nesting       |
| `flat`         | nested JSON       | Only for apps that parse the JSON themselves       |

`yobaconf-run` defaults to `envvar` and rejects `flat` (you can't export nested JSON
as env vars). Pick `dotnet` for .NET apps that call `builder.Configuration` directly.

If a specific binding must come through under a mandated name that doesn't derive
cleanly (e.g. `AWS_ACCESS_KEY_ID`, `PGHOST`), the binding editor accepts a
per-template alias override.

## 3. Wire the runner into your Dockerfile

`yobaconf-run` ships as its own chiseled image at
`ghcr.io/stdray/yobaconf-runner:<tag>`. COPY it into your app image at build time:

```dockerfile
# Builder / runtime stages for your app go here…

# Final stage:
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=ghcr.io/stdray/yobaconf-runner:<tag> /yobaconf-run /usr/local/bin/yobaconf-run
COPY --from=build /app/publish /app
WORKDIR /app
ENTRYPOINT ["/usr/local/bin/yobaconf-run", \
    "--endpoint=https://yobaconf.3po.su", \
    "--template=envvar", \
    "--tag=env=prod", \
    "--tag=project=yobapub", \
    "--", \
    "./MyApp"]
```

Pin the runner tag to a specific GitVersion (e.g. `0.2.3`) rather than `latest` — the
runner is invoked every container start, and a breaking exit-code change could flip
your orchestrator's restart policy silently.

## 4. Pass the API key at runtime, not build time

The api-key is the only secret in play; don't bake it into the image. Pass it via
container env at `docker run` / `compose up`:

```yaml
services:
  yobapub:
    image: ghcr.io/stdray/yobapub:1.2.3
    environment:
      YOBACONF_API_KEY: ${YOBACONF_API_KEY}
    restart: on-failure
```

```bash
export YOBACONF_API_KEY="…"
docker compose up -d
```

`yobaconf-run` picks `YOBACONF_API_KEY` up automatically (env-var fallback for every
flag). You can also override endpoint/template/tags from the environment without
editing the image:

- `YOBACONF_ENDPOINT`
- `YOBACONF_TEMPLATE`
- (tags remain flag-only — they're usually static per image)

## 5. Per-host / per-instance tags

For runtime overrides (e.g. tracing level on a specific instance), inject the host or
instance tag dynamically:

```dockerfile
ENTRYPOINT ["/usr/local/bin/yobaconf-run", \
    "--tag=env=prod", \
    "--tag=project=yobapub", \
    "--tag=host=$HOSTNAME", \
    "--", \
    "./MyApp"]
```

Then in the web UI, add a binding with `{env:prod, project:yobapub, host:box-07}` for
`log-level=Trace`. Resolve on that host picks up the overlay (TagCount=3 beats the
2-tag baseline); other hosts continue with the default.

If multiple instances need the same override, use a role tag instead of a host tag —
`{project, role}` is more stable than bare hostnames.

## 6. Health-probe behaviour

`yobaconf-run` exits with a specific code on every failure class:

- `0` — child exited cleanly (we mirror the code)
- `2` — server returned 409 Conflict. Admin needs to add a more-specific overlay.
  The diagnostic (naming the colliding bindings) is printed to stderr.
- `3` — 403 Forbidden. The request tag-vector doesn't satisfy the api-key's
  RequiredTags. Check the key's scope in `/admin/api-keys`.
- `4` — connection / fetch error (DNS, timeout, 5xx, 401 unknown token).
- `5` — invalid runner args (missing `--endpoint`, malformed `--tag`, etc.).

Because `yobaconf-run` exits **before** invoking the child on any of 2–5, the
container never reaches its normal "ready" state. Docker / compose / Kubernetes
restart-on-failure puts it in a crash loop with the stderr message visible via
`docker logs` — Caddy's active healthcheck sees 5xx on the upstream and drops the
service from the pool. No partial startup, no silent-default fallbacks.

## 7. SIGTERM forwarding

When the container is asked to stop (`docker stop`, orchestrator drain), the OS
sends SIGTERM to PID 1, which is `yobaconf-run`. The runner forwards to the child,
waits up to 2s for clean exit, then escalates to SIGKILL. The child's exit code
propagates up as `yobaconf-run`'s exit code — healthchecks see the real status.

If your app has a longer graceful-shutdown budget (e.g. 30s for in-flight requests),
set `stop_grace_period` on the compose service; the runner itself doesn't cap the
child's time.

## 8. Putting it together — compose example

```yaml
services:
  yobapub:
    image: ghcr.io/stdray/yobapub:1.2.3
    environment:
      YOBACONF_API_KEY: ${YOBAPUB_YOBACONF_API_KEY}
      HOSTNAME: ${HOSTNAME:-unknown}
    restart: on-failure
    stop_grace_period: 30s
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/health"]
      interval: 10s
      timeout: 2s
      retries: 3
```

Your app inside the container now sees `DB_HOST`, `DB_PORT`, `LOG_LEVEL`, etc. —
everything YobaConf resolves for `{env:prod, project:yobapub, host:<machine>}` —
as though they were in a plain `.env` file.
