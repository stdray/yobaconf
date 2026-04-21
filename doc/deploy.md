# YobaConf deployment guide

Bootstrap walkthrough for deploying YobaConf to a fresh host. YobaConf is the **second**
service in the stack — **yobalog deploys first** and owns the one-time host setup
(`apt install caddy`, firewall rules, `/etc/caddy/Caddyfile` scaffold). This guide
assumes yobalog is already live on the same host.

If yobalog isn't live yet, follow its `doc/deploy.md` first. That guide installs Caddy
and opens the 80/443 firewall ports; from there everything below reuses the same
infrastructure.

## Prerequisites

- [ ] Host with Docker installed, reachable via SSH from GitHub Actions.
- [ ] Caddy running on the host (installed by yobalog's first-time bootstrap).
- [ ] DNS: A-record `yobaconf.3po.su` → server public IP.
- [ ] GitHub repository secrets (Settings → Secrets and variables → Actions):
    - `DEPLOY_HOST` — server hostname or IP.
    - `DEPLOY_USERNAME` — SSH user (typically same as yobalog's).
    - `DEPLOY_PASSWORD` — SSH password (sudo-capable on the host).
    - `GHCR_DEPLOY_USERNAME` / `GHCR_DEPLOY_TOKEN` — GHCR read-token for `docker pull`
      on the host. Same values yobalog uses.
    - `YOBACONF_MASTER_KEY` — AES-256 master key for Secret encryption.
      **Generate locally**: `openssl rand -base64 32` (Phase C requires this; Phase A
      ignores it but the deploy workflow already passes it through).
    - `YOBACONF_ADMIN_USERNAME` — admin login for the cookie-auth UI.
    - `YOBACONF_ADMIN_PASSWORD_HASH` — generated via
      `dotnet run --project src/YobaConf.Web -- --hash-password <plaintext>`
      (or `dotnet YobaConf.Web.dll --hash-password <plaintext>` in the container).
      **Never store plaintext in secrets** — the hash is PBKDF2-SHA256 and
      safe to expose; plaintext isn't.

## Step 1 — generate the admin password hash

Locally:
```bash
dotnet run --project src/YobaConf.Web -- --hash-password 'your-strong-password'
```

Output: `pbkdf2$100000$<salt>$<hash>`. Copy the whole string into the GitHub secret
`YOBACONF_ADMIN_PASSWORD_HASH`. Plaintext goes nowhere else.

## Step 2 — add the Caddy fragment

SSH to the server, open the central Caddyfile:

```bash
sudo nano /etc/caddy/Caddyfile
```

Append the block from `infra/Caddyfile.fragment` in this repo. It looks like this
(port 8082 stays; port 8081 is yobaconf's slot from the shared allocation table):

```caddy
yobaconf.3po.su {
    reverse_proxy 127.0.0.1:8081 {
        flush_interval -1
    }
    encode gzip zstd
    log {
        output file /var/log/caddy/yobaconf.access.log {
            roll_size 50mb
            roll_keep 5
        }
        format json
    }
}
```

Reload Caddy:
```bash
sudo systemctl reload caddy
```

Verify the fragment syntax before reload: `sudo caddy validate --config /etc/caddy/Caddyfile`.
Caddy fetches the TLS cert on the first HTTPS request to the new host — no manual certbot
step.

## Step 3 — ensure the data directory exists

The container mounts `/opt/yobaconf/data` from the host for the SQLite file:

```bash
sudo mkdir -p /opt/yobaconf/data
sudo chown 1654:1654 /opt/yobaconf/data    # uid for the chiseled `app` user
```

The deploy job's `docker run` line already contains `-v /opt/yobaconf/data:/app/data`.

## Step 4 — trigger the deploy

From your local clone of yobaconf:

```bash
git tag -f deploy
git push origin deploy --force
```

GitHub Actions picks up the tag:
1. `test` job: format verify + Cake Test target (unit + integration).
2. `publish` job: Cake DockerPush target (build + DockerSmoke + push to ghcr.io).
3. `deploy` job: SSH to the host, `docker pull`, stop-and-remove old container,
   start new one with the port binding and env vars injected from secrets.

The force-push on the tag is intentional — `deploy` is a "moving pointer" tag we reuse
for each deployment. Each push is logged in the Actions tab with the commit SHA that was
deployed.

## Step 5 — verify the deploy

From anywhere:

```bash
curl https://yobaconf.3po.su/health
# → {"status":"healthy"}

curl https://yobaconf.3po.su/version
# → {"semVer":"0.1.X-...","shortSha":"abc1234","commitDate":"..."}

curl https://yobaconf.3po.su/ready
# → {"status":"ready"}         (SQLite file created, schema applied)
# → 503 if the DB isn't reachable yet; retry after ~5s
```

Open `https://yobaconf.3po.su/Login` in a browser — you should see the sign-in form.
Log in with the username + password you set in Step 1. Land on the empty tree, click
`New from paste`, and import your first config.

## Step 6 — add API keys for runtime clients

YobaConf reads API keys from `appsettings.json` under `ApiKeys:Keys[]`. To add one in
production without rebuilding, use a mounted `appsettings.Production.json` or env-var
overrides. Pattern:

```bash
# Via env vars on docker run (in the CI deploy job already):
ApiKeys__Keys__0__Token=<random-22-char-shortguid>
ApiKeys__Keys__0__RootPath=projects/animemov-bot
ApiKeys__Keys__0__Description=animemov-bot prod runtime
```

Generate a token (locally on any machine with .NET):
```bash
dotnet run --project src/YobaConf.Web -- --hash-password 'unused'  # just to confirm build works
# Then in a dotnet-script or PowerShell:
[Convert]::ToBase64String([guid]::NewGuid().ToByteArray()).Replace('+','-').Replace('/','_').TrimEnd('=')
```

Put the result in the client app's own config: `X-YobaConf-ApiKey: <token>` or
`?apiKey=<token>` in the URL.

## Rollback

If a deploy breaks prod, roll back by deploying a previous commit:

```bash
git checkout <prev-known-good-sha>
git tag -f deploy
git push origin deploy --force
```

The CI runs the full pipeline on the older commit — tests + smoke + deploy. No special
"rollback" workflow; `deploy` tag is the only trigger.

## Deployment asymmetries vs yobalog (for reference)

- **Port**: yobaconf = 8081, yobalog = 8082. Allocation table in
  `infra/Caddyfile.fragment` of each repo.
- **Caddy SSE**: yobalog has SSE on `/api/ws/{id}/tail`, so its Caddyfile has
  `flush_interval -1`. yobaconf gained the same directive pre-emptively (commit
  `8cf5388`) in case a future htmx-sse feature lands.
- **Deploy gating**: yobalog deploys first because it brings Caddy to the host.
  yobaconf's deploy.md (this file) assumes that's already done.
- **Data volume**: yobalog = `/opt/yobalog/data` (per-workspace `.db` files),
  yobaconf = `/opt/yobaconf/data` (one `yobaconf.db`).

## Next services

The port table is `8080..8082` used; `8083+` free. When deploying the next service
(`animemov-bot-cs`, `kpvotes`, `yobaspeach`, ...):
1. Reserve the next free port in **every** repo's `infra/Caddyfile.fragment` (keep
   the allocation table synced — comment header is identical across fragments).
2. DNS A-record for the new subdomain.
3. Append the Caddyfile block to the central config; reload.
4. Deploy via that service's own `deploy` tag flow.

No central infra repo is needed — each service owns its fragment in its own repo; the
central `/etc/caddy/Caddyfile` is assembled by hand from the per-service fragments.
If that gets too tedious with 5+ services we can promote a shared infra repo later.
