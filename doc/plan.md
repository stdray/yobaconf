# YobaConf: План работ и стратегия тестирования

## Ядро тестов — детерминизм пайплайна резолвинга

Главный инвариант (spec §4): один и тот же вход (`path` + API key + состояние БД на момент запроса) обязан давать ровно один JSON-результат. Это естественно покрывается **snapshot-тестами**: фикстура (ноды + HOCON + переменные + секреты) + запрос → ожидаемый JSON. Любое расхождение = регрессия в пайплайне.

Snapshot — первичный инструмент, property-тесты поверх (например, scope API-ключей) — когда домен стабилизируется.

## Фазы

- [ ] **Фаза A.0 — Bootstrap.** Репо-гигиена и тулинг, без кода приложения. Цель — задать тон один раз, чтобы не переделывать по каждому PR.
    - [x] `.gitignore`, `.gitattributes`, `.editorconfig` скопированы из yobalog (стек идентичен: .NET 10 + bun + Tailwind; LiteDB-файлы ловятся существующим `*.db` паттерном).
    - [x] `global.json` — пин .NET 10 SDK 10.0.202 (rollForward = latestFeature), синхронно с yobalog.
    - [x] `Directory.Build.props` на корне: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`, `<AnalysisMode>All</AnalysisMode>`, `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`, `<InvariantGlobalization>true</InvariantGlobalization>`.
    - [x] `Directory.Packages.props` — Central Package Management включён (`<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>`). Versions пока только тестовые (xunit, FluentAssertions, coverlet, Microsoft.NET.Test.Sdk). HOCON-пакет добавится после Phase A.1.
    - [~] Solution skeleton: `YobaConf.slnx` + `src/YobaConf.Core` + `tests/YobaConf.Tests` созданы. `src/YobaConf.Web` пока отложен до Phase A основного (Razor Pages появится вместе с API endpoint и UI).
    - [ ] Фронт-бутстрап рядом с `YobaConf.Web`: `package.json`, `tsconfig.json` со всеми strict-флагами + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes` + `noImplicitOverride` + `noUnusedLocals` + `noPropertyAccessFromIndexSignature`, `tailwind.config.js`, заглушки `ts/admin.ts` и `ts/app.css`, `bun.lock` (текстовый, закоммичен).
    - [ ] `biome.json` — lint + format для TS; табы/ширина 2; `noExplicitAny: error`, `noNonNullAssertion: error`, `useConst: error`. `useEditorconfig` отключён по той же причине, что и в yobalog (biome не парсит `indent_size = tab`).
    - [ ] MSBuild target в `YobaConf.Web.csproj`: `BeforeTargets="Build"` + `Condition="'$(Configuration)' == 'Release'"` → `bun install --frozen-lockfile && bun run build`. В Debug MSBuild не трогает фронт.
    - [ ] CI skeleton (`.github/workflows/ci.yml`): `bun install` + `biome check` + `bun run typecheck` + `dotnet restore` + `dotnet format --verify-no-changes` + `dotnet build -c Release` + `dotnet test -c Release`.
    - [ ] Smoke-test: всё зелёное локально.

- [x] **Фаза A.1 — HOCON-гейт (блокирующая проверка).** Пройдена 2026-04-21.
    - [x] Выбран пакет: `Hocon` 2.0.4 от akkadotnet/HOCON. Все нужные API живы в примерах: `HoconConfigurationFactory.ParseString`, `HoconParser.Parse(hocon, ConfigResolver)` с callback'ом на `include` (File/Resource/Url/кастом), `.WithFallback(other)`, `${?var}` substitution, `.PrettyPrint(indent)`. Активность репы низкая (последний функциональный релиз 2021), но API стабильный (Akka.NET core). Подробности + downgrade path — `decision-log.md`.
    - [x] Пакет прописан в `Directory.Packages.props`.
    - [ ] Smoke-test с реальным dotnet restore — отложен до Phase A.0 solution skeleton (когда появится `.csproj` на кого повесить PackageReference).

- [ ] **Фаза A — dog-food ready.** API `GET /v1/conf/{path}` с Fallthrough и резолвингом переменных (без секретов, без истории), API-ключи со scoped `RootPath`, минимальный read-only UI (дерево + просмотр HOCON + результирующий JSON), bootstrap из `appsettings.json`. На этой фазе YobaConf уже хостит конфиги для других своих проектов.
    - [~] Доменные типы в Core: `NodePath` готов (slug-регex + `.`/`/` round-trip). Остаются: `HoconNode` (готов как record), `Variable`, `ApiKey`, `IConfigStore` (stub), `ResolveResult`.
    - [ ] `SqliteConfigStore` — linq2db-бэкенд, таблицы `Nodes`, `Variables`, `Secrets`, `ApiKeys`, `AuditLog` (spec §3). WAL mode. `Path` UNIQUE index; `(ScopePath, Key)` UNIQUE для Variables/Secrets; `TokenHash` UNIQUE для ApiKeys.
    - [ ] Resolve pipeline (spec §4) как чистая функция `(NodePath, IConfigStoreSnapshot) → ResolveResult` — без HTTP, без auth. Fallthrough (routing, не merge) → подгрузка variables/secrets по ScopePath (ancestor-or-equal запрошенного path, не best-match) → **include preprocessor с DFS+cycle detection** (свой, не HOCON callback): для каждого `include "abs-path"` проверка `dir(target) ancestor-or-equal dir(including)`, check visited → `CyclicIncludeException`, раскрытие → плоский текст → склейка `variables-hocon + flat-hocon` → единый `ParseString` (substitution at parse-time) → сериализация в JSON.
    - [ ] Snapshot-тесты по чек-листу ниже.
    - [ ] `GET /v1/conf/{path}` — парсит внешний `.`-путь в `NodePath`, гоняет pipeline, отдаёт JSON + ETag.
    - [ ] API-ключи scoped — `IApiKeyStore` + `ConfigApiKeyStore` (plaintext в `appsettings`, master-ключи; LiteDB-стор появится в Phase B). `X-YobaConf-ApiKey` header + `?apiKey=` query.
    - [ ] Bootstrap из `appsettings.json` — seed admin, мастер-AES-ключ через env var (`YOBACONF_MASTER_KEY`), API-ключ для YobaLog в `appsettings`.
    - [ ] Минимальный read-only UI — Razor Pages: дерево узлов, просмотр HOCON, результирующий JSON. Tailwind + DaisyUI, `data-theme="dark"`. Cookie-auth с единственным админом из `appsettings`.
    - [ ] Self-observability — CLEF-клиент к YobaLog, выделенный workspace `yobaconf-ops` (spec §11). Рекурсия невозможна по конструкции (YobaLog не знает про YobaConf).

- [ ] **Фаза B — редактирование.** CRUD по нодам в UI с Monaco, audit log (immutable history), soft delete, diff текущей версии с предыдущей. **Optimistic locking** через `ContentHash` column на Nodes/Variables/Secrets: UPDATE WHERE Id AND ContentHash; rows=0 → conflict modal (three-way diff, inspired by stdray.Obsidian ConflictSolverService). **Unified timeline UI** (spec §7): история по path объединяет Nodes+Variables+Secrets+ApiKeys в одну ленту — пользователь не переключает вкладки по сущностям.
- [ ] **Фаза C — секреты.** Variables & Secrets с AES-256, маскирование в UI (`******`), отдельный scope ключей для доступа к секретам. Мастер-ключ — env var, не в БД/конфиге.
- [ ] **Фаза D — клиентские SDK.** .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun.
- [ ] **Фаза E — push для сверхнагрузок.** Экспорт собранного JSON в Redis/Consul/S3.

## Тестовое покрытие до фазы A

- [~] **Резолвинг (snapshot tests):** пока нет JSON-сериализатора — первые тесты сделаны на типизированных accessors (`GetString`/`GetInt`). Snapshot-движок подключится, когда появится сериализатор HOCON→JSON.
- [x] **Fallthrough:** `FallthroughTests.cs` — exact hit, missing leaf → ancestor, missing subtree → root-level, no ancestor → null, ancestor chain root-to-leaf ordering, skipped middle nodes. 6 тестов.
- [x] **`NodePath` валидация slug:** `NodePathTests.cs` — валидные сегменты, round-trip между `.`- и `/`-нотацией, отклонение прописных букв / underscore / ведущий-тире / коротких сегментов / пробелов / точек внутри сегмента, `$`-системный префикс, `Parent` walk, `Root == default`, value-equality. 15 тестов.
- [x] **HOCON merge smoke** (Phase A.1 follow-up): `HoconMergeTests.cs` — `.WithFallback` скаляры, deep objects, substitution через concat-before-parse, optional `${?var}`, required substitution бросает at parse-time. 5 тестов.
- [ ] **Циклические инклуды между нодами:** `A → B → A` (мутуальные sibling-includes) ловится с понятной ошибкой, включает цепочку путей. Реализуется в preprocess-стадии через `HashSet<NodePath> visited`. Property test: произвольный цикл произвольной длины детектится.
- [ ] **Include scope violation:** `include` target вне разрешённой области (descendant, sibling-subtree, self) → понятная ошибка. Permutation tests на all-vs-all комбинациях 4-нодного дерева.
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
- [ ] **Циклические ссылки в HOCON substitutions:** `a = ${b}, b = ${a}` в одном тексте — ловится парсером? Нужна защита от глубокой рекурсии (DoS). Проверить тестом в Phase A.
- [ ] **Клиентские SDK:** обёртки для .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun. Phase D.
- [ ] **Rate limiting:** не реализуем в MVP (удалено из §11 self-observability). Phase E polish.
- [ ] **Perf target:** soft goal "p99 < 50ms resolve+serialize на 1k нод" — проверяется когда появится нагрузочный тест.
