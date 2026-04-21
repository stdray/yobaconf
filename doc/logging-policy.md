# Logging policy

Политика логирования для всех сервисов стека, шиппящих события в yobalog. yobaconf — reference implementation; пути/имена ниже из его tree. Консьюмер берёт этот файл, подставляет своё имя сервиса и своё `appsettings.json` и получает совместимый с остальным стеком лог-поток.

## Целевая инфраструктура

- **Workspace в yobalog — один общий** (`apps-prod`). Различение по CLEF-полю `App`. Причина: yobalog MVP не умеет cross-workspace KQL-запросы (каждый workspace = свой `.db`, query engine per-WS). Общий WS — единственный способ склеить события по `TraceId` между сервисами (user→bot→yobaconf→...). Изоляция через retention-policies, не через WS-разделение.
- **Транспорт**: CLEF over HTTP → yobalog Seq-compat endpoint `{host}/compat/seq/api/events/raw`, auth через `X-Seq-ApiKey` header. См. yobalog `doc/spec.md` §2 для non-.NET language compatibility.
- **Propagation**: W3C `traceparent` header. В .NET встроено в `HttpClient` + ASP.NET Core из коробки — ничего дополнительно настраивать не надо, TraceId'ы между сервисами склеятся автоматом.

## .NET-сервис — wiring

### Packages

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Seq.Extensions.Logging" Version="9.0.0" />
```

```xml
<!-- <ProjectName>.Web.csproj -->
<ItemGroup>
  <PackageReference Include="Seq.Extensions.Logging" />
</ItemGroup>
```

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// Activity tracking: stamp TraceId/SpanId on every event. W3C traceparent
// propagation is on by default in HttpClient + ASP.NET Core — cross-service
// call chains join automatically.
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

// Seq-compat sink — disabled on empty ServerUrl (dev without user-secrets,
// integration tests via WebApplicationFactory).
var seqUrl = builder.Configuration["YobaLog:ServerUrl"];
var seqKey = builder.Configuration["YobaLog:ApiKey"];
if (!string.IsNullOrWhiteSpace(seqUrl))
    builder.Logging.AddSeq(seqUrl, apiKey: seqKey);

// Structured access-log — skip probe endpoints (health/ready hit every few
// seconds from Docker healthcheck / k8s probe / Caddy; pure noise).
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
    // No headers (tokens live there — Authorization, Cookie, X-*-ApiKey).
    // No bodies (noise + potential PII + CLEF size bloat).
});

// ... your own service wiring (AddRazorPages, AddAuthentication, etc.) ...

var app = builder.Build();

// Access-log applies only for non-probe paths.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/health")
        && !ctx.Request.Path.StartsWithSegments("/ready"),
    branch => branch.UseHttpLogging());

// Static+request enrichment: wrap every request in a scope with App/Env/Ver/Sha/
// Host/Ip. `User` is added in a second scope after UseAuthentication so that
// authenticated requests get a user-tagged event while anonymous ones don't fake
// a null value.
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object?>
    {
        ["App"]  = "<your-app-name>",             // short, lowercase, stable identifier
        ["Env"]  = app.Environment.EnvironmentName,
        ["Ver"]  = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
        ["Sha"]  = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
        ["Host"] = Environment.MachineName,
        ["Ip"]   = ctx.Connection.RemoteIpAddress?.ToString(),
    }))
    {
        await next();
    }
});

app.UseAuthentication();
app.UseAuthorization();

// User-enrichment: only when authenticated. Avoids `User: null` on every anon
// event (those are filtered in yobalog KQL by `has_cs User` instead).
app.Use(async (ctx, next) =>
{
    var userName = ctx.User.Identity?.Name;
    if (!string.IsNullOrEmpty(userName))
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        using (logger.BeginScope(new Dictionary<string, object?> { ["User"] = userName }))
            await next();
    }
    else
    {
        await next();
    }
});

// ... rest of your pipeline (UseEndpoints, MapRazorPages, etc.) ...

app.Run();
```

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.HttpLogging": "Information"
    }
  },
  "YobaLog": { "ServerUrl": "", "ApiKey": "" }
}
```

Обязательные три уровня:
- `Default: Information` — твой собственный код пишет на Info+.
- `Microsoft.AspNetCore: Warning` — давит Kestrel-шум (`Request starting HTTP/2 GET ...` / `Request finished ...` — 2 event/request, без фильтра это 50% volume'а).
- `Microsoft.AspNetCore.HttpLogging: Information` — `UseHttpLogging` пишет в эту категорию, её **не** должен давить Warning-фильтр выше.

### Ограничения scope-enrichment'а

Scope-middleware покрывает request-path события (~95% volume'а prod-сервиса). **Startup-события и background-hosted-services** проходят ВНЕ scope'а — у них не будет `App`/`Env`/`Ver`/`Sha`/`Host`. Fallback в KQL: фильтровать по `SourceContext startswith "YourApp."` — имя ассемблери в `SourceContext` остаётся детерминированным.

Если 5% startup/background-логов без `App`-поля — это критично (например, у сервиса много long-running hosted services), более радикальные варианты:
- Свитч на Serilog с `Enrich.WithProperty("App", "...")` — enricher срабатывает на ВСЕХ событиях, не scope-based.
- Wrap `app.Run()` в scope главного треда — scope распространяется через AsyncLocal на request-таски. Менее надёжно: flows through ExecutionContext, но startup-события ДО `app.Run()` всё равно без scope'а.

Для MVP — scope-middleware + `SourceContext`-filter хватает.

## Field taxonomy

Naming — **PascalCase, короткое и однозначное**. Seq/CLEF community convention — PascalCase (Serilog message-template property-destructuring даёт `UserId` → `UserId`, вся экосистема Seq-клиентов на этом). camelCase / snake_case валидны парсером yobalog, но рвут compat с existing tooling.

### Static (на каждом request-событии, через scope)

| Поле | Источник | Пример | Назначение |
|---|---|---|---|
| `App` | scope, hardcode | `"yobaconf"` | Фильтрация в shared workspace |
| `Env` | scope, `IWebHostEnvironment.EnvironmentName` | `"Production"` | Отличить prod от staging |
| `Ver` | scope, env var `APP_VERSION` | `"0.1.0-42"` | Привязка к билду |
| `Sha` | scope, env var `GIT_SHORT_SHA` | `"139fc78"` | Привязка к коммиту |
| `Host` | scope, `Environment.MachineName` | `"yobaconf"` | Multi-instance / после restart |

### Activity-tracking (на каждом событии, автоматом через `ActivityTrackingOptions`)

| Поле | Источник | Назначение |
|---|---|---|
| `TraceId` | `Activity.Current.TraceId` | Cross-service correlation |
| `SpanId` | `Activity.Current.SpanId` | Scope внутри одного сервиса |

### Per-request (scope, после `UseAuthentication`)

| Поле | Источник | Назначение |
|---|---|---|
| `Ip` | `ctx.Connection.RemoteIpAddress` | Post-ForwardedHeaders = реальный клиент |
| `User` | `ctx.User.Identity?.Name` | Только для authenticated-requests (иначе scope не открывается) |

### Access-log (через `UseHttpLogging`)

| Поле | Источник |
|---|---|
| `Method` | auto |
| `Path` | auto |
| `StatusCode` | auto |
| `Duration` | auto |

### Domain-specific

Стампить в code-path'ах, где поле имеет смысл. Пример для yobaconf (`/v1/conf/{path}` resolve):

```csharp
logger.LogInformation(
    "Resolved {Path} -> {Resolved} ({Status}) in {ElapsedMs}ms, etag={ETag}, key={Key}, scope={Scope}, incs={Incs}, depth={Depth}",
    requestedPath, resolvedPath, 200, elapsedMs, etag, apiKey.Prefix, apiKey.RootPath, includesCount, fallthroughDepth);
```

Message-template parser Seq/CLEF распакует `{Path}`, `{Resolved}`, и т.д. в CLEF-properties автоматом. Одно Info-событие с 10 полями лучше чем 10 строк логов с одним полем.

## Что НЕ логировать

- **Headers** — не включаем в `HttpLoggingFields`. Там `Authorization`, `Cookie`, `X-*-ApiKey`.
- **Bodies (request/response)** — шум + potential PII + раздувание CLEF-batch'а.
- **Probe endpoints** — `/health`, `/ready` skip'аются через `UseWhen` ветвление.
- **Stack traces на Info** — только Warning+ (структурированный exception в CLEF через `@x`).
- **Plaintext API-ключи / пароли / AES-мастер-ключ** — даже в error-events. Стамповать только prefix (6 chars) или хэш.

## Retention-policies (workspace-level, в yobalog UI)

Когда workspace будет настраиваться (после появления 2-3 сервисов):

- `@l == Error or @l == Fatal` → 90 days
- `@l == Warning` → 30 days
- `@l == Information and Path startswith "/v1/"` → 7 days (access-log волume)
- default → 30 days

Per-app carveouts по мере надобности:
`where App == "yobaconf" and SourceContext startswith "YobaConf.Core.Resolve" → 3 days` (если resolve-log'и окажутся volumetric).

## Secrets

### Dev — `dotnet user-secrets`

user-secrets резолвятся только в Development env — prod к ним доступа не имеет по конструкции.

```bash
dotnet user-secrets init --project src/<YourApp>.Web
dotnet user-secrets set "YobaLog:ServerUrl" "https://yobalog.3po.su/compat/seq" --project src/<YourApp>.Web
dotnet user-secrets set "YobaLog:ApiKey"    "<dev-workspace-key>"                --project src/<YourApp>.Web
```

### Prod — GitHub secrets + env vars в docker run

**Секреты в repo settings** (Settings → Secrets and variables → Actions):
- `YOBALOG_SERVER_URL` — `https://yobalog.3po.su/compat/seq` (без `/api/events/raw` — провайдер допишет)
- `YOBALOG_API_KEY` — API-key с ingest-scope на workspace `apps-prod`, создаётся в yobalog admin UI, plaintext показывается один раз

**Проброс в `docker run` через `appleboy/ssh-action` envs:-passthrough**. Не инлайнить секреты через `${{ secrets.X }}` в body скрипта — bash раскрывает `$` в значении секрета (PBKDF2-хэш формата `pbkdf2$100000$...` ломается под `set -u` как "unbound variable $1"). Правильный паттерн:

```yaml
- name: Deploy via SSH
  uses: appleboy/ssh-action@v1
  env:
    YOBALOG_URL: ${{ secrets.YOBALOG_SERVER_URL }}
    YOBALOG_KEY: ${{ secrets.YOBALOG_API_KEY }}
    # ... other secrets
  with:
    host: ${{ secrets.DEPLOY_HOST }}
    username: ${{ secrets.DEPLOY_USERNAME }}
    password: ${{ secrets.DEPLOY_PASSWORD }}
    envs: YOBALOG_URL,YOBALOG_KEY
    script: |
      set -euo pipefail
      docker run -d \
        --name "<your-app>" \
        -e YobaLog__ServerUrl="$YOBALOG_URL" \
        -e YobaLog__ApiKey="$YOBALOG_KEY" \
        ...
```

Envs-passthrough: GH Actions кладёт значения в env step'а → ssh-action форвардит через SSH → bash подставляет через shell-variable `$YOBALOG_URL`. Bash раскрывает shell-variable один раз и не re-scan'ит раскрытое содержимое — `$` в секрете остаётся литералом.

## Non-.NET apps

Любой CLEF/Seq-compat writer работает:
- **Python**: `seqlog` / `python-seq-logging` — endpoint `https://yobalog.3po.su/compat/seq`, header `X-Seq-ApiKey`, формат events — CLEF/Seq-envelope.
- **JS/TS**: `@datalust/winston-seq` — то же.
- **Go**: `seq-logging` или прямой CLEF NDJSON POST.

Language-specific compat-матрица — yobalog `doc/spec.md` §2. Field-taxonomy выше (`App`/`Env`/`Ver`/`Sha`/`Host`/`Ip`/`User`/...) одинакова по всем языкам — парсер yobalog не знает про .NET specific'ы.

## Cross-refs

- yobalog `doc/spec.md` §1-2 — endpoint contract, client compatibility matrix
- yobaconf `doc/decision-log.md` "Self-observability via Seq.Extensions.Logging" — почему MEL-native, не Serilog
- yobaconf `doc/decision-log.md` "Logging policy: shared workspace, field taxonomy" — обоснование решений этого файла
- yobaconf `doc/deploy.md` Step 6 — конкретная пошаговая выкладка секретов для одного сервиса
