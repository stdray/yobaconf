# Observability policy

Политика для всех сервисов стека, шиппящих **логи и спаны** в yobalog. yobaconf — reference implementation; пути/имена ниже из его tree. Консьюмер берёт этот файл, подставляет своё имя сервиса и свои `appsettings.json` + GitHub secrets — получает совместимый с остальным стеком observability-поток (логи на Seq-compat + OTLP-трейсы).

Файл исторически называется `logging-policy.md`; после Phase C.5 покрывает и tracing. Переименовать можно; cross-refs и commit'ы ссылаются на старое имя, менять лень.

## Два канала, один workspace

- **Логи** (CLEF) ← `Seq.Extensions.Logging` → `{host}/compat/seq/api/events/raw` (Seq-compat namespace yobalog'а).
- **Трейсы** (OTLP HTTP/Protobuf) ← `OpenTelemetry.Exporter.OpenTelemetryProtocol` → `{host}/v1/traces`.
- Оба — в один yobalog workspace (`apps-prod`). Auth — один и тот же `X-Seq-ApiKey` header, один и тот же ключ (yobalog's OTLP ingestion reuses Seq-compat auth).

### Что куда писать

| Тип события | Канал | Зачем |
|---|---|---|
| Stage/latency/status внутри одного request'а (resolve pipeline, SQL, include-chain) | **Trace (span)** | Waterfall + attributes → диагностика "что долго" / "что сломалось" без reconstructуров по message'ам |
| Cross-service correlation (user → bot → yobaconf → ...) | **Trace (W3C traceparent)** | Один TraceId через всю цепочку — склеивается в yobalog автоматом |
| Business event (node upserted, api-key rotated, admin login) | **Log (Info)** | Audit trail + retention policy по `@l` |
| Error / unusual path (include cycle, bad apikey scope, DB write failure) | **Log (Warning / Error)** | Alerts + stack trace через `@x` |
| Access log (Method/Path/Status/Duration per request) | **Trace (HTTP root span, automatic)** | `AspNetCoreInstrumentation` уже эмитит; дублировать в логах = 2x volume |

**Правило:** per-request per-stage латентность — span-attributes, не log-fields. Одноразовые события бизнес-уровня — логи.

## Целевая инфраструктура

- **Workspace в yobalog — один общий** (`apps-prod`). Различение по CLEF-полю `App` в логах и `service.name` resource-атрибуту в трейсах. Причина: yobalog MVP не умеет cross-workspace KQL-запросы (каждый workspace = свой `.db`, query engine per-WS). Общий WS — единственный способ склеить события по `TraceId` между сервисами. Изоляция через retention-policies, не через WS-разделение.
- **Propagation**: W3C `traceparent` header. В .NET встроено в `HttpClient` + ASP.NET Core из коробки — ничего дополнительно настраивать не надо, TraceId'ы между сервисами склеятся автоматом.
- **Non-.NET**: стандартный OTel SDK с `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` + `OTEL_EXPORTER_OTLP_HEADERS=X-Seq-ApiKey=...`. См. yobalog `doc/spec.md` §2.

## .NET-сервис — wiring

### Packages

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Seq.Extensions.Logging" Version="9.0.0" />
<PackageVersion Include="OpenTelemetry" Version="1.15.1" />
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.15.1" />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.1" />
<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
```

```xml
<!-- <ProjectName>.Web.csproj -->
<ItemGroup>
  <PackageReference Include="Seq.Extensions.Logging" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
</ItemGroup>
```

### Program.cs (logs side)

```csharp
using Seq.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// 1. Stamp TraceId/SpanId on every log event. W3C traceparent propagation is
//    on by default in HttpClient + ASP.NET Core — cross-service call chains
//    join automatically, and log events land on the trace waterfall via their
//    TraceId in yobalog.
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

// 2. Seq-compat sink with static enrichers. AddSeq's `enrichers:` parameter
//    accepts `Action<EnrichingEvent>[]` — each lambda fires on EVERY event
//    (startup, background, IHostedService — all of it) and stamps
//    CLEF-properties via evt.AddOrUpdateProperty. This is the MEL-native
//    equivalent of Serilog's Enrich.WithProperty — 100% coverage without
//    scope middleware.
//
//    Note: the `EnrichingEvent` type is the public API of the callback;
//    Seq.Extensions.Logging's internal `LogEvent` is NOT exposed and casting
//    to `Action<LogEvent>` will not compile. README examples use `evt` as
//    shorthand and can mislead.
//
//    Gated on non-empty ServerUrl: dev without user-secrets / integration
//    tests see ServerUrl="" and skip registration (console-only logging).
var seqUrl = builder.Configuration["YobaLog:ServerUrl"];
var seqKey = builder.Configuration["YobaLog:ApiKey"];
if (!string.IsNullOrWhiteSpace(seqUrl))
{
    // Config-driven static props come from YobaLog:Properties subsection.
    // Consumer projects override ONLY `App` — others are runtime-computed.
    var props = builder.Configuration.GetSection("YobaLog:Properties")
        .GetChildren()
        .Where(c => !string.IsNullOrEmpty(c.Value))
        .ToDictionary(c => c.Key, c => (object)c.Value!);

    props["Env"]  = builder.Environment.EnvironmentName;
    props["Ver"]  = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
    props["Sha"]  = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local";
    props["Host"] = Environment.MachineName;

    builder.Logging.AddSeq(
        serverUrl: seqUrl,
        apiKey: seqKey,
        enrichers:
        [
            .. props.Select(kv => (Action<EnrichingEvent>)(evt => evt.AddOrUpdateProperty(kv.Key, kv.Value))),
        ]);
}
```

### Program.cs (tracing side — Phase C.5)

Обычно в `ConfigureServices` (или inline в Program.cs — роли не играет). Gate чисто config-driven — никаких `IsEnvironment("Testing")` проверок (plan.md invariant).

```csharp
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Phase C.5: OTLP traces → yobalog /v1/traces. Gate: Enabled + non-empty
// endpoint. Default appsettings has both off, so dev/tests skip registration;
// prod turns both on via env vars injected from GitHub secrets. ASP.NET Core
// root HTTP span comes automatically from AspNetCoreInstrumentation —
// domain code emits child spans via its own ActivitySource (see below).
var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", false);
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
if (otelEnabled && !string.IsNullOrWhiteSpace(otlpEndpoint))
{
    var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "<your-app>";
    var serviceVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
    var apiKey = builder.Configuration["YobaLog:ApiKey"] ?? string.Empty;

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName, serviceVersion: serviceVersion))
        .WithTracing(tracing => tracing
            .AddSource(MyActivitySources.DomainSourceName, MyActivitySources.StorageSourceName)
            .AddAspNetCoreInstrumentation(opts =>
            {
                // Skip probe noise — /health /ready get hit every few seconds by
                // Docker healthcheck / Caddy probe / k8s readiness; filling
                // yobalog's spans.db with these is pure waste.
                opts.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/health")
                    && !ctx.Request.Path.StartsWithSegments("/ready");
            })
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otlpEndpoint);
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Headers = $"X-Seq-ApiKey={apiKey}";
            }));
}
```

### ActivitySource скелет

`Core/Observability/ActivitySources.cs` — const-имена для `AddSource(...)` + static instances для instrumentation call-site'ов:

```csharp
using System.Diagnostics;

namespace MyApp.Core.Observability;

// Keep sources granular by DOMAIN AREA, not per-method. 2-3 sources is typical
// (one per subsystem — e.g. Resolve + Storage.Sqlite for yobaconf; Ingestion +
// Query + Storage + Retention for yobalog). Every extra source = one extra
// line in AddSource(...) registration, and listener-lookup cost on each span
// start.
public static class MyActivitySources
{
    public const string DomainSourceName = "MyApp.Domain";
    public const string StorageSourceName = "MyApp.Storage";

    public static readonly ActivitySource Domain = new(DomainSourceName);
    public static readonly ActivitySource Storage = new(StorageSourceName);
}
```

Call-site pattern (domain code):

```csharp
using var activity = MyActivitySources.Domain.StartActivity("myapp.resolve");
activity?.SetTag("myapp.input-id", inputId);
// ... do work ...
activity?.SetTag("myapp.output-size", result.Length);
```

`StartActivity` возвращает null когда listener не подключён (Enabled=false, тесты без OTel-wiring) → `using (null)` — no-op, zero cost. Tags — по OTel-convention: `<namespace>.<attribute>` snake-case-dot.

### Access-log + per-request enrichment (logs side, остаётся нужным)

```csharp
// Structured access-log — skip probe endpoints (health/ready hit every
// few seconds from Docker healthcheck / k8s / Caddy; pure noise). Note:
// trace root span ALREADY captures Method/Path/Status/Duration via
// AspNetCoreInstrumentation. HttpLogging is kept for the audit-log use
// case (who hit what when, filterable by KQL outside trace UI) and for
// when tracing is off.
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
    // No headers (tokens live there — Authorization, Cookie, X-*-ApiKey).
    // No bodies (noise + potential PII + CLEF size bloat).
});

var app = builder.Build();

app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/health")
        && !ctx.Request.Path.StartsWithSegments("/ready"),
    branch => branch.UseHttpLogging());

// Per-request scope: `Ip` always, `User` only when authenticated. Static
// props (App/Env/Ver/Sha/Host) are already on the event via AddSeq enrichers
// above — this only adds request-scope-dynamic fields.
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    var scopeProps = new Dictionary<string, object?>
    {
        ["Ip"] = ctx.Connection.RemoteIpAddress?.ToString(),
    };
    var userName = ctx.User.Identity?.Name;
    if (!string.IsNullOrEmpty(userName))
        scopeProps["User"] = userName;

    using (logger.BeginScope(scopeProps))
        await next();
});
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
  "YobaLog": {
    "ServerUrl": "",
    "ApiKey": "",
    "Properties": {
      "App": "<your-app-name>"
    }
  },
  "OpenTelemetry": {
    "Enabled": false,
    "ServiceName": "<your-app-name>",
    "OtlpEndpoint": ""
  }
}
```

Обязательные три log-level:
- `Default: Information` — твой собственный код пишет на Info+.
- `Microsoft.AspNetCore: Warning` — давит Kestrel-шум (`Request starting HTTP/2 GET ...` / `Request finished ...` — 2 event/request, без фильтра это 50% volume'а).
- `Microsoft.AspNetCore.HttpLogging: Information` — `UseHttpLogging` пишет в эту категорию, её **не** должен давить Warning-фильтр выше.

Per-project конфиг — только `YobaLog:Properties:App` и `OpenTelemetry:ServiceName` (оба должны матчить — это одно и то же имя сервиса для логов и трейсов). Если нужны дополнительные постоянные лог-тэги (region/cluster) — добавляются в `YobaLog:Properties`, enricher подхватит автоматом. Соответствующие resource-атрибуты трейсов — через `.ConfigureResource(r => r.AddAttributes(...))` в `AddOpenTelemetry` блоке.

### Покрытие enricher-подхода

AddSeq-enricher срабатывает на **каждом** CLEF-событии — request-path, startup, background `IHostedService`, `IHostApplicationLifetime`-callback'и, всё. В отличие от scope-middleware подхода (покрывает только request-pipeline). Никаких fallback'ов через `SourceContext startswith` не нужно.

`Ip` и `User` остаются в scope-middleware потому что они request-контекстные — enricher без `HttpContext.Current` их не вытянет.

## Field taxonomy

Naming — **PascalCase, короткое и однозначное**. Seq/CLEF community convention — PascalCase (Serilog message-template property-destructuring даёт `UserId` → `UserId`, вся экосистема Seq-клиентов на этом). camelCase / snake_case валидны парсером yobalog, но рвут compat с existing tooling.

### Static (на каждом событии, через AddSeq-enricher)

| Поле | Источник | Пример | Назначение |
|---|---|---|---|
| `App` | `YobaLog:Properties:App` из appsettings | `"yobaconf"` | Фильтрация в shared workspace |
| `Env` | runtime, `IWebHostEnvironment.EnvironmentName` | `"Production"` | Отличить prod от staging |
| `Ver` | runtime, env var `APP_VERSION` | `"0.1.0-42"` | Привязка к билду |
| `Sha` | runtime, env var `GIT_SHORT_SHA` | `"139fc78"` | Привязка к коммиту |
| `Host` | runtime, `Environment.MachineName` | `"yobaconf"` | Multi-instance / после restart |

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

Дублирует root HTTP span из `AspNetCoreInstrumentation` частично (span имеет `http.request.method` / `url.path` / `http.response.status_code` / duration из span-timestamp'а). Оставлять ли HttpLogging после включения трейсов — выбор trade-off'а:
- **Оставить**: audit-log через KQL остаётся работать когда tracing off / sampled out, независимо от span-retention policy.
- **Убрать**: один источник истины, меньше volume в shared workspace, но grep по /Path у пользователей через KQL ломается при sampled-out трейсах.

Рекомендация: оставить HttpLogging с narrow-set (`Method|Path|StatusCode|Duration`) — volume маленький, audit ценится.

### Domain-specific — через span-attributes, не через логи

**Было** (Phase-A early draft): один info-лог `Resolved {Path} -> {Status} in {ElapsedMs}ms ...` с 10 domain-полями на каждый request. **Откатили** в пользу трейсов (решение commit `da7d53c`, Phase C.5) — span'ы покрывают те же 5 use-case'ов (slow-start diagnosis / 304-vs-200 / include-chain / DB-regression / cross-service correlation) строго лучше: per-stage breakdown, waterfall-UI, automatic cross-service TraceId.

Правильный паттерн domain-observability теперь:

```csharp
using var activity = MyActivitySources.Domain.StartActivity("myapp.resolve");
activity?.SetTag("myapp.path", requestedPath);

using (MyActivitySources.Domain.StartActivity("myapp.fallthrough-lookup"))
{
    bestMatch = FindBestMatch(requestedPath);
}
activity?.SetTag("myapp.resolved", bestMatch.Path);

using (MyActivitySources.Domain.StartActivity("myapp.parse"))
{
    parsed = Parse(combined);
}
// ... etc
```

Результат в yobalog'е — waterfall `myapp.resolve` с 4+ child-span'ами, каждый со своей длительностью и tags. Info-логи остаются для **discrete business events** (node created / api-key rotated / admin login succeeded) — не для per-request детализации.

Полезные span-attribute соглашения:
- `<app>.<noun>` для identifier'ов (`yobaconf.path`, `yobaconf.resolved`)
- `<app>.<noun>.count` для числовых agregate (`yobaconf.includes.count`, `yobaconf.variables.count`)
- `db.system = sqlite`, `db.operation = select` / `update` — стандартные OTel semantic-conventions для DB spans (опционально, но удобно для cross-service уни-фильтров)
- **НЕ** стампить персональные данные в tags (tags индексируются, дорого чистить).

## Что НЕ логировать / НЕ трейсить

- **Headers** — не включаем в `HttpLoggingFields`. Там `Authorization`, `Cookie`, `X-*-ApiKey`.
- **Bodies (request/response)** — шум + potential PII + раздувание CLEF-batch'а.
- **Probe endpoints** — `/health`, `/ready` skip'аются через `UseWhen` в HttpLogging middleware + через `AspNetCoreInstrumentation.Filter` в OTel trace-wiring. Оба фильтра нужны: разные уровни pipeline'а.
- **Stack traces на Info** — только Warning+ (структурированный exception в CLEF через `@x` / в span через `activity.SetStatus(Error, ex.Message)`).
- **Plaintext API-ключи / пароли / AES-мастер-ключ / значения секретов** — ни в логах, ни в span-attributes. Стамповать только prefix (6 chars) или хэш.
- **Per-request access-event как Info-лог** — это задача span'а (automatic root HTTP span из AspNetCoreInstrumentation). Не дублировать.
- **Per-stage durations как логи** — это задача child-span'ов. Логи = discrete events, трейсы = per-request timeline.

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
- `YOBALOG_API_KEY` — API-key с ingest-scope на workspace `apps-prod`, создаётся в yobalog admin UI, plaintext показывается один раз. Переиспользуется для OTLP-traces (yobalog использует тот же `X-Seq-ApiKey` header на `/v1/traces`).
- `<APP>_OTEL_ENABLED` — literal `"true"` чтобы включить tracing; любое другое / пусто = off.
- `<APP>_OTLP_ENDPOINT` — `https://yobalog.3po.su/v1/traces` (полный URL, без автоматического дописывания; OTel exporter не трогает path).

**Проброс в `docker run` через `appleboy/ssh-action` envs:-passthrough**. Не инлайнить секреты через `${{ secrets.X }}` в body скрипта — bash раскрывает `$` в значении секрета (PBKDF2-хэш формата `pbkdf2$100000$...` ломается под `set -u` как "unbound variable $1"). Правильный паттерн:

```yaml
- name: Deploy via SSH
  uses: appleboy/ssh-action@v1
  env:
    YOBALOG_URL: ${{ secrets.YOBALOG_SERVER_URL }}
    YOBALOG_KEY: ${{ secrets.YOBALOG_API_KEY }}
    OTEL_ENABLED: ${{ secrets.MYAPP_OTEL_ENABLED }}
    OTLP_ENDPOINT: ${{ secrets.MYAPP_OTLP_ENDPOINT }}
    # ... other secrets
  with:
    host: ${{ secrets.DEPLOY_HOST }}
    username: ${{ secrets.DEPLOY_USERNAME }}
    password: ${{ secrets.DEPLOY_PASSWORD }}
    envs: YOBALOG_URL,YOBALOG_KEY,OTEL_ENABLED,OTLP_ENDPOINT
    script: |
      set -euo pipefail
      docker run -d \
        --name "<your-app>" \
        -e YobaLog__ServerUrl="$YOBALOG_URL" \
        -e YobaLog__ApiKey="$YOBALOG_KEY" \
        -e OpenTelemetry__Enabled="$OTEL_ENABLED" \
        -e OpenTelemetry__OtlpEndpoint="$OTLP_ENDPOINT" \
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

- yobalog `doc/spec.md` §1-2 — endpoint contract (Seq-compat + OTLP), client compatibility matrix
- yobaconf `doc/decision-log.md` "Self-observability via Seq.Extensions.Logging" — почему MEL-native, не Serilog (logs stack)
- yobaconf `doc/decision-log.md` "Logging policy: shared workspace, field taxonomy" — обоснование решений по полям/workspace
- yobaconf `doc/decision-log.md` "Phase C.5 OTel self-emission: applied, gate removed" — tracing wiring + ActivitySource rationale + gate policy
- yobaconf `doc/deploy.md` Step 6+7 — конкретная пошаговая выкладка секретов (logs + OTLP) для одного сервиса
- yobaconf `plan.md` invariant "Никакого IsEnvironment('Testing') в production-коде" — почему все gates config-driven
