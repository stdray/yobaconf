# YobaConf: План работ и стратегия тестирования

## Ядро тестов — детерминизм пайплайна резолвинга

Главный инвариант (spec §4): один и тот же вход (`path` + API key + состояние БД на момент запроса) обязан давать ровно один JSON-результат. Это естественно покрывается **snapshot-тестами**: фикстура (ноды + HOCON + переменные + секреты) + запрос → ожидаемый JSON. Любое расхождение = регрессия в пайплайне.

Snapshot — первичный инструмент, property-тесты поверх (например, scope API-ключей) — когда домен стабилизируется.

## Фазы

- [x] **Фаза A.0 — Bootstrap.** Репо-гигиена, тулинг, сборочный pipeline (Cake + GitVersion + Docker). Без кода приложения. Цель — задать тон один раз, чтобы не переделывать по каждому PR.
    - [x] `.gitignore`, `.gitattributes`, `.editorconfig` скопированы из yobalog (стек идентичен: .NET 10 + bun + Tailwind; SQLite-файлы ловятся существующим `*.db` паттерном).
    - [x] `global.json` — пин .NET 10 SDK 10.0.202 (rollForward = latestFeature), синхронно с yobalog.
    - [x] `Directory.Build.props` на корне: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`, `<AnalysisMode>All</AnalysisMode>`, `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`, `<InvariantGlobalization>true</InvariantGlobalization>`.
    - [x] `Directory.Packages.props` — Central Package Management включён. Hocon 2.0.4 + Hocon.Configuration 2.0.4 + linq2db.SQLite.MS 5.4.1 + transitive CVE pins.
    - [x] Solution skeleton: `YobaConf.slnx` + `src/YobaConf.Core` + `src/YobaConf.Web` (минимальный Razor Pages webapp с Index + Error + `_Layout`) + `tests/YobaConf.Tests`. Общие property — в `Directory.Build.props`.
    - [x] Фронт-бутстрап рядом с `YobaConf.Web`: `package.json` (concurrently + daisyui + tailwindcss + typescript + @biomejs/biome — monaco-editor добавится в Phase B при интеграции редактора), `tsconfig.json` со всеми strict-флагами включая `noUncheckedIndexedAccess` / `exactOptionalPropertyTypes` / `noImplicitOverride` / `noPropertyAccessFromIndexSignature`, `tailwind.config.js` (content: только `.cshtml` + `.ts`, `.cs` не сканируем — AGENTS.md запрещает HTML в .cs), заглушки `ts/admin.ts` и `ts/app.css`, `bun.lock` сгенерирован `bun install`.
    - [x] `biome.json` — lint + format для TS; табы/ширина 2; `noExplicitAny: error`, `noNonNullAssertion: error`, `useConst: error`, `noUnusedVariables/Imports: error`; `useEditorconfig` отключён (biome не парсит `indent_size = tab`).
    - [x] MSBuild target в `YobaConf.Web.csproj`: `BeforeTargets="Build"` + `Condition="'$(Configuration)' == 'Release'"` → `bun install --frozen-lockfile && bun run build`. В Debug MSBuild не трогает фронт.
    - [x] Dev-loop: `./build.sh --target=Dev` (или `build.ps1 -Target Dev`) запускает `bun run dev` + `dotnet watch` в одной консоли через System.Diagnostics.Process. Ctrl+C убивает оба process-tree. Заменило двух-оконный `run_dev.ps1` (удалён).
    - [x] CI skeleton (`.github/workflows/ci.yml`): frontend + format checks, затем `./build.sh --target=Test` → Cake pipeline (Clean → Restore → Version → Build → Test). На main-push + tag `deploy` — отдельный `publish` job: `./build.sh --target=DockerPush --dockerPush=true` (включает DockerSmoke task: curl контейнера 30s timeout). На tag `deploy` — третий job `deploy` с SSH (secrets: `DEPLOY_HOST`, `GHCR_DEPLOY_USERNAME/TOKEN`, `YOBACONF_MASTER_KEY`).
    - [x] `GitVersion.yml` + `.config/dotnet-tools.json` (Cake.Tool 5.0.0 + GitVersion.Tool 6.4.0). Скопировано с yobapub-паттерна, `next-version: 0.1.0`.
    - [x] `build.cake` + `build.sh` + `build.ps1` — tasks Clean/Restore/Version/Build/Test/Docker/DockerSmoke/DockerPush. См. decision-log "Build pipeline: Cake + GitVersion".
    - [x] `src/YobaConf.Web/Dockerfile` — two-stage: SDK 10.0 + bun installer → `runtime-deps:10.0-noble-chiseled`. Self-contained linux-x64 publish. GitVersion build-args пробрасываются в env.
    - [x] `.dockerignore` — node_modules, bin/obj, wwwroot-генерёжка, *.db, .git, tests, doc/, AGENTS/CLAUDE.md.
    - [x] Smoke-test: всё зелёное локально — `./build.sh --target=Test` 35/35 ✓ (Clean 0.5s + Restore 0.5s + Version 0.4s + Build 1.8s + Test 1.0s = 4.2s total). Docker-target локально не проверен (Docker daemon не запущен в текущей сессии) — будет проверен первым CI-прогоном.

- [x] **Фаза A.1 — HOCON-гейт (блокирующая проверка).** Пройдена 2026-04-21.
    - [x] Выбран пакет: `Hocon` 2.0.4 + `Hocon.Configuration` 2.0.4 от akkadotnet/HOCON. Все нужные API живут в примерах; substitution резолвится at parse-time (см. decision-log 2026-04-21 "Hocon 2.0.4 резолвит substitutions at parse-time").
    - [x] Пакеты прописаны в `Directory.Packages.props`.
    - [x] Smoke-test с реальным dotnet restore + dotnet test: 35/35 passed (Phase A.0 smoke-test закрыл этот пункт).

- [ ] **Фаза A — dog-food ready.** API `GET /v1/conf/{path}` с Fallthrough и резолвингом переменных (без секретов, без истории), API-ключи со scoped `RootPath`, минимальный read-only UI (дерево + просмотр HOCON + результирующий JSON), bootstrap из `appsettings.json`. На этой фазе YobaConf уже хостит конфиги для других своих проектов.
    - [x] Доменные типы в Core готовы: `NodePath`, `HoconNode`, `Variable`, `Secret`, `VariableSet`, `IConfigStore`, `ResolveResult`, `NodeNotFoundException`. `ApiKey` — Phase A API-слой.
    - [x] **`SqliteConfigStore`** — linq2db-бэкенд, таблицы `Nodes`, `Variables`, `Secrets` (ApiKeys/AuditLog — Phase B). WAL mode per-connection. `Path` UNIQUE на Nodes; partial `(ScopePath, Key) WHERE IsDeleted=0` UNIQUE index на Variables/Secrets. CREATE IF NOT EXISTS schema bootstrap (idempotent, replay на каждом startup'е). Admin API: Upsert/SoftDelete для Node/Variable/Secret, ContentHash = sha256 hex computed при upsert. 14 integration тестов через tmp `.db` (roundtrip, overwrite, soft-delete, resurrect, scope isolation, schema idempotency, end-to-end через ResolvePipeline).
    - [x] **Resolve pipeline (spec §4)** — `ResolvePipeline.Resolve(NodePath, IConfigStore) → ResolveResult { Json, ETag }`. Композит: Fallthrough → VariableScopeResolver → HoconVariableRenderer → IncludePreprocessor → склейка vars+flat → `ParseString` → `HoconJsonSerializer` → sha256→16-hex ETag. 12 snapshot/e2e тестов в `ResolvePipelineTests` + 7 в `HoconVariableRendererTests`. Secrets отложены до Phase C (spec §14 "Фаза A — dog-food ready: без секретов").
    - [ ] Snapshot-тесты по чек-листу ниже.
    - [ ] **HOCON → JSON сериализатор** в Core — обход `HoconRoot` tree → `System.Text.Json` (`JsonNode` / `JsonDocument`). Блокер для preview-панели и snapshot-тестов резолвинга. Тесты на scalars / nested objects / arrays / substitutions / dotted-path keys / edge cases.
    - [x] **`GET /v1/conf/{**urlPath}`** — парсит `.`-path через `NodePath.ParseUrl`, гоняет `ResolvePipeline`, отдаёт `200 OK` + JSON + ETag. Поддержка `If-None-Match` → `304 Not Modified`. Scope check через `NodePath.IsAncestorOf` до lookup'а (403 before 404). Инвалидный slug → 400.
    - [x] **API-ключи scoped** — `IApiKeyStore` + `ConfigApiKeyStore` (plaintext в `appsettings`, master-ключи; SQLite-стор появится в Phase B). `X-YobaConf-ApiKey` header + `?apiKey=` query. Constant-time token compare через `CryptographicOperations.FixedTimeEquals`.
    - [x] **ForwardedHeaders wiring (Caddy)** — `UseForwardedHeaders()` перед `UseHttpsRedirection`, `KnownProxies = { loopback }` только. Без этого за Caddy `HttpContext.Request.IsHttps == false`, redirect loops, Secure-cookie flag неправильный. 12 integration-тестов через `WebApplicationFactory<Program>`: 401 missing/wrong key, 403 out-of-scope + boundary (yobaapp vs yobaapplication), 404 missing node, 200 happy path, 304 ETag match, 200 на stale IfNoneMatch, dot-to-slash parse, 400 invalid slug, query-string apiKey, Fallthrough через endpoint.
    - [~] Bootstrap из `appsettings.json` — seed admin через `AdminOptions` + `AdminPasswordHasher` (PBKDF2-SHA256 @100k iterations) + `--hash-password` CLI в `Program.cs` для генерации хэша. Мастер-AES-ключ через env var — отложен до Phase C (secrets). API-ключ для YobaLog — отложен до Phase F yobalog (CLEF-клиент).
    - [x] **Минимальный read-only UI** — Razor Pages: `/Index` (дерево путей через `IConfigStore.ListNodePaths()`), `/Node?path=...` (RawContent + fallthrough-notice если ноды нет), `/Import` (paste-форма). Tailwind + DaisyUI. Cookie-auth (`AddAuthentication(Cookie)` + fallback-policy `RequireAuthenticatedUser`), PBKDF2 admin credential из appsettings. `[IgnoreAntiforgeryToken]` на Login + Import — Phase B включит CSRF. HOCON-Prism подсветка отложена в follow-up (80-строчный порт TextMate grammar).
    - [x] **JSON preview-панель резолвинга** — `language-json` Prism block с pretty-printed JSON + ETag badge + fallthrough-link если Path не существовал и резолв ушёл на ancestor. Ошибки резолва (parse/include) показываются alert'ом.
    - [x] **IConfigStoreAdmin interface** — отдельный write-contract (Upsert/SoftDelete). `SqliteConfigStore` имплементирует оба; DI регистрирует один singleton на оба intf'а так, чтобы resolve pipeline (`IConfigStore`) не мог мутировать, admin UI (`IConfigStoreAdmin`) мог.
    - [x] **Import converters (Core, чистые функции):** `JsonToHoconConverter` (validate+pretty-print через `JsonDocument`), `DotenvToHoconConverter` (ручной парсер ~150 строк — quoted/unquoted/escapes/export-prefix/line-numbered errors), `YamlToHoconConverter` (YamlDotNet 17.0.1 + walker с type-inference для plain scalars: bool/null/long/double/fallback string). Единый `ImportException` wrap'ит нативные parser-exceptions. 32 теста: 6 JSON (valid/invalid/nested/array/empty/round-trip), 13 dotenv (quoted/escapes/comments/export/invalid-key/trailing-backslash), 13 YAML (mapping/sequence/nested/plain-numeric/quoted-stays-string/null-like/bool/float/escape/invalid/anchors-expanded/mixed-structure).
    - [x] **Paste-import UI** — `/Import` форма: TargetPath + Source textarea + Format dropdown (json/yaml/env) + Preview/Save actions. Preview зовёт converter, Save дополнительно пишет `IConfigStoreAdmin.UpsertNode`. Закрывает bootstrap-поток для dog-food — оператор заливает existing `.env` / `values.yaml` без ручного переписывания в HOCON.
    - [ ] Self-observability — CLEF-клиент к YobaLog, выделенный workspace `yobaconf-ops` (spec §12). Рекурсия невозможна по конструкции (YobaLog не знает про YobaConf).
    - [ ] **Deploy-wiring под Caddy-based HTTPS (spec §11):** `UseForwardedHeaders` с `KnownProxies = { IPAddress.Loopback }` в `YobaConfApp.Configure()` — перед `UseHttpsRedirection`, чтобы ASP.NET видел `X-Forwarded-Proto=https` от loopback-proxy'а. Без этого `IsHttps == false`, redirect loops, cookie `Secure`-flag неправильный. Intergration-тест: request с `X-Forwarded-Proto=https` → `HttpContext.Request.IsHttps == true`.
    - [ ] **First-time host bootstrap (doc, не код):** `doc/deploy.md` с пошаговыми инструкциями — DNS setup, `apt install caddy`, начальный Caddyfile из `infra/Caddyfile.fragment`, firewall rules, первый `git tag deploy`. Для себя и для yobalog-Клода как образец.

- [ ] **Фаза B — редактирование.** CRUD по нодам в UI с CodeMirror 6 + HOCON StreamLanguage-tokenizer (~150 строк, порт sabieber TextMate grammar), audit log (immutable history), soft delete, diff через `@codemirror/merge`. **Optimistic locking** через `ContentHash` column на Nodes/Variables/Secrets: UPDATE WHERE Id AND ContentHash; rows=0 → conflict modal (three-way diff, inspired by stdray.Obsidian ConflictSolverService — та же CodeMirror 6 экосистема). **Unified timeline UI** (spec §7): история по path объединяет Nodes+Variables+Secrets+ApiKeys в одну ленту — пользователь не переключает вкладки по сущностям.
- [ ] **Фаза C — секреты.** Variables & Secrets с AES-256, маскирование в UI (`******`), отдельный scope ключей для доступа к секретам. Мастер-ключ — env var, не в БД/конфиге.
- [ ] **Фаза C.5 — OTel self-emission** (gated на yobalog Phase F; см. `decision-log.md` 2026-04-21 "OpenTelemetry self-emission: gated on yobalog Phase F"). YobaConf эмитит spans своего `ResolvePipeline` + HTTP endpoint'ов + SQLite writes через OTLP HTTP/Protobuf. Экспорт — любой OTLP-collector (в первую очередь yobalog после Phase F, альтернатива — Seq 2023.2+ / Jaeger / Tempo). Off-by-default через `OpenTelemetry:Enabled` в `appsettings.json`.
    - [ ] Директория `src/YobaConf.Core/Telemetry/`: `YobaConfActivitySource` (static ActivitySource с именем `"YobaConf"`) + span-names как const.
    - [ ] `AddOpenTelemetry()` wiring в `YobaConfApp.ConfigureServices`: `.WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("YobaConf").AddOtlpExporter(...))`. Gated на `OpenTelemetry:Enabled == true`. В `IsEnvironment("Testing")` — skip (как `UseHttpsRedirection`), чтобы юнит-тесты не шумели.
    - [ ] `ActivitySource.StartActivity()` в hot-path'ах `ResolvePipeline`: `resolve` (root span), дочерние `fallthrough-lookup` / `variables-resolve` / `include-preprocess` / `hocon-parse` / `json-serialize` / `etag-compute`. Attributes: `yobaconf.path`, `yobaconf.includes.count`, `yobaconf.variables.count`, `yobaconf.cache.hit`.
    - [ ] SQLite manual instrumentation на границе `SqliteConfigStore` методов (`OpenTelemetry.Instrumentation.Sqlite` не существует — см. yobalog decision-log 903fd4a). ~10-15 строк: `ActivitySource.StartActivity("sqlite.find-node")` вокруг `FindNode` + `AppendAudit`.
    - [ ] Конфиг: `OpenTelemetry:Enabled` (default `false`), `OpenTelemetry:OtlpEndpoint` (default `http://localhost:4318/v1/traces`), `OpenTelemetry:ServiceName` (default `"yobaconf"`).
    - [ ] Unit-тесты: `ActivityListener` fixture verifies spans эмитятся в correct order с expected attributes. 4-6 тестов (resolve happy path / missed node / cycle detection / scope violation).
    - [ ] Документация в README: how to point YobaConf at yobalog / Seq / Jaeger через env var `OpenTelemetry__OtlpEndpoint`.
- [ ] **Фаза D — клиентские SDK.** .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun.
- [ ] **Фаза E — push для сверхнагрузок.** Экспорт собранного JSON в Redis/Consul/S3.

## Тестовое покрытие до фазы A

- [x] **Резолвинг (snapshot tests):** `ResolvePipelineTests` покрывает end-to-end fixture → JSON + ETag для простой ноды, fallthrough, variable substitution, variable inheritance, explicit include, ETag determinism across key-order permutations, ETag change on content/variable change, full mix (fallthrough + vars + include), empty-object node, unresolved substitution propagation. 12 тестов. HOCON→JSON сериализатор + HoconVariableRenderer уже закрыты.
- [x] **HOCON → JSON сериализатор:** `HoconJsonSerializer.SerializeToJson(HoconRoot, pretty)` + `SerializeToNode(HoconRoot) → JsonNode?`. Canonical output: ordinal-sorted object keys (ETag determinism), arrays keep HOCON order. 16 тестов: scalars (string/int/double/bool), nested objects, dotted-path keys, arrays, key-sort determinism, substitutions resolved, null literal vs quoted "null", empty containers, nested arrays-of-objects, escape sequences, pretty printing, negatives + large integers, `SerializeToNode` manipulable tree, empty-object literal. Документировали Hocon 2.0.4 quirk: unquoted doubles репортят `Type=String` — `ConvertString` re-типизирует через Raw.
- [ ] **Import converters** (pre-Phase A prerequisite): `JsonToHocon` / `YamlToHocon` / `DotenvToHocon`. Блокирует paste-import UI. Тесты на типовые паттерны каждого формата + edge cases (escaping, quoted strings, comments).
- [x] **Fallthrough:** `FallthroughTests.cs` — exact hit, missing leaf → ancestor, missing subtree → root-level, no ancestor → null, ancestor chain root-to-leaf ordering, skipped middle nodes. 6 тестов.
- [x] **`NodePath` валидация slug:** `NodePathTests.cs` — валидные сегменты, round-trip между `.`- и `/`-нотацией, отклонение прописных букв / underscore / ведущий-тире / коротких сегментов / пробелов / точек внутри сегмента, `$`-системный префикс, `Parent` walk, `Root == default`, value-equality. 15 тестов.
- [x] **HOCON merge smoke** (Phase A.1 follow-up): `HoconMergeTests.cs` — `.WithFallback` скаляры, deep objects, substitution через concat-before-parse, optional `${?var}`, required substitution бросает at parse-time. 5 тестов.
- [x] **Циклические инклуды между нодами:** `A → B → A` и `A → B → C → A` ловятся через `CyclicIncludeException` с полной цепочкой путей. Реализация — `IncludePreprocessor` с inflight `HashSet<NodePath>` + `Stack<NodePath>` для reporting. 2 теста.
- [x] **Include scope violation:** descendant / sibling-subtree / self-include → `IncludeScopeViolationException` с понятным сообщением (named parties). 3 теста (по одному на каждый вариант).
- [x] **Include target not found:** отсутствующая нода → `IncludeTargetNotFoundException` с target-полем. 2 теста (root + included-from-existing).
- [x] **Unsupported HOCON forms:** `include file(...)` / `classpath(...)` / `url(...)` / `required(...)` / relative `"../"` → `UnsupportedIncludeSyntaxException`. 5 тестов (Theory + relative).
- [x] **Positive include cases:** ancestor, sibling-same-dir, nested (3 уровня), dup-via-two-paths, trailing-comment, case-sensitive `include` keyword. 6 тестов.
- [ ] **API-ключ scope (property test):** ключ на `yobaproj.yobaapp` видит `yobaproj.yobaapp.dev`, не видит `yobaproj.otherapp`; граничные случаи (exact match на `RootPath`, префикс-collision типа `yobaproj.yobaapplication` не должен проходить).
- [ ] **ETag:** одинаковый вход → одинаковый хеш; `304 Not Modified` корректно; изменение любого включённого фрагмента инвалидирует.
- [ ] **HOCON include-resolution:** инклуд несуществующего фрагмента — ошибка; защита от бесконечной глубины (DoS).

## Инварианты, которые легко нарушить (читать перед кодом)

- **Нет self-config.** Сам YobaConf конфигурируется только из `appsettings.json`. Попытка положить что-либо в YobaConf-ноду `$system/yobaconf` и читать оттуда при старте — бутстрап-цикл. Мастер-ключ AES — env var, не файл.
- **Нет YobaLog → YobaConf.** YobaConf → YobaLog (CLEF) — единственное направление. В YobaLog `spec.md` §1 зафиксировано, что его конфиг только из `appsettings.json` — цикл невозможен по конструкции.
- **Secrets в AuditLog — всегда зашифрованные.** Spec §7. Utility-тест: смотрим в `.db`-файл после записи секрета, убеждаемся, что plaintext там не лежит.
- **Slug-regex для имён нод.** `^[a-z0-9][a-z0-9-]{1,39}$`, точка запрещена (разделитель пути), `$`-префикс зарезервирован для системных нод. Синхронно с workspace ID в YobaLog.
- **API-ключ scope — проверка по сегментам, не по подстроке.** Ключ на `yobaproj.yobaapp` не должен пускать на `yobaproj.yobaapplication`. Сравнение — по элементам пути после split'а.
- **Детерминизм pipeline.** Snapshot-инварианты — главная защита от регрессий. Любое изменение форматирования итогового JSON, порядка мержа или resolve — меняет snapshot, видно в diff.
- **Локализация с первого дня.** Все user-facing строки через `IStringLocalizer`. Пока i18n-каркаса нет — все строки **литерально на английском ASCII**. CI будет иметь non-ASCII check на `ts/` и `Pages/` (как в yobalog).
- **UI-селекторы: `data-testid` обязателен.** Как в yobalog (`tests/YobaConf.E2ETests/` появится в Phase B): `page.GetByTestId(...)`, никаких `GetByText`, `GetByRole(Name=...)`, CSS-класс-селекторов на chrome. Display-строки — цели локализации, сломаются на первом же переводе.
- **Frontend build — Release-only.** Debug не зовёт `bun` из MSBuild; для dev — параллельные watcher'ы (`dotnet watch` + `bun run dev`).

## Расщепление документации (когда появится репозиторий)

Готово:
- [x] `doc/spec.md` — чистая спека (§1-§12 без прогресса и плана).
- [x] `doc/plan.md` — этот файл, прогресс + тест-чеклист + инварианты.
- [x] `doc/decision-log.md` — лог архитектурных решений. Новые записи сверху.

## Открытые вопросы

- [x] **БД:** SQLite + linq2db (решено 2026-04-21, см. decision-log).
- [x] **Workspaces поверх path-tree:** нет, paths сами по себе namespace (решено 2026-04-21).
- [x] **Variables vs Secrets storage:** две отдельные таблицы, не единая с `IsSecret`-флагом (решено 2026-04-21).
- [x] **Include-семантика:** каждый `.hocon` = отдельная нода; `dir(target) ancestor-or-equal dir(including)`; siblings в той же dir разрешены → возможны циклы → runtime DFS cycle detection; абсолютные пути only; резолвинг — своя preprocess-стадия до HOCON parse. Auto ancestor-merge убран. (Финализировано 2026-04-21 после двух итераций, см. decision-log "Include-семантика финализирована".)
- [x] **Конкурентные правки в UI:** optimistic locking через `ContentHash` column + three-way merge modal (решено 2026-04-21).
- [x] **Secret access control:** API-ключ на path даёт доступ к resolved JSON со всеми секретами; отдельной permission нет (решено 2026-04-21, `spec.md` §4).
- [x] **ETag формула:** `first-16-hex-chars(sha256(rendered-json))`, strong (решено 2026-04-21, `spec.md` §4.6).
- [x] **API-key token format:** ShortGuid (22 chars, 122 бита) + sha256 hash + 6-char prefix для UI — синхронно с yobalog (решено 2026-04-21, `spec.md` §2).
- [x] **Восстановление soft-deleted:** через новую запись из `AuditLog` snapshot'а (решено 2026-04-21, `spec.md` §7).
- [x] **Мастер-ключ AES** — env var `YOBACONF_MASTER_KEY` (`spec.md` §2).
- [x] **Лог аудита** — immutable, хранится всегда (`spec.md` §7).

Остаются:
- [x] **Циклические ссылки в HOCON substitutions:** `a = ${b}, b = ${a}` и 3-way цикл `a → b → c → a` — парсер бросает `HoconParserException` at parse-time. 2 теста в `HoconMergeTests.cs`. Защита от DoS по глубине рекурсии — на стороне Hocon 2.0.4, не нашей.
- [ ] **Клиентские SDK:** обёртки для .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun. Phase D.
- [ ] **Rate limiting:** не реализуем в MVP (удалено из §12 self-observability). Phase E polish.
- [ ] **Perf target:** soft goal "p99 < 50ms resolve+serialize на 1k нод" — проверяется когда появится нагрузочный тест.
