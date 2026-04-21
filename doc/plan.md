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
    - [ ] Доменные типы в Core: `NodePath` (slug-валидация per-segment, spec §8 regex), `HoconNode`, `Variable`, `ApiKey`, `IConfigStore`, `ResolveResult`.
    - [ ] `LiteDbConfigStore` — LiteDB-бэкенд, коллекции `Nodes`, `Variables`, `ApiKeys` (spec §3). Path-индекс уникальный.
    - [ ] Resolve pipeline (spec §4) как чистая функция `(NodePath, IConfigStoreSnapshot) → ResolveResult` — без HTTP, без auth. Fallthrough → сбор родителей → рендер variables в HOCON → склейка `variables + parent + ... + leaf` → единый `ParseString` (substitution резолвится at parse-time, см. decision-log 2026-04-21) → сериализация в JSON.
    - [ ] Snapshot-тесты по чек-листу ниже.
    - [ ] `GET /v1/conf/{path}` — парсит внешний `.`-путь в `NodePath`, гоняет pipeline, отдаёт JSON + ETag.
    - [ ] API-ключи scoped — `IApiKeyStore` + `ConfigApiKeyStore` (plaintext в `appsettings`, master-ключи; LiteDB-стор появится в Phase B). `X-YobaConf-ApiKey` header + `?apiKey=` query.
    - [ ] Bootstrap из `appsettings.json` — seed admin, мастер-AES-ключ через env var (`YOBACONF_MASTER_KEY`), API-ключ для YobaLog в `appsettings`.
    - [ ] Минимальный read-only UI — Razor Pages: дерево узлов, просмотр HOCON, результирующий JSON. Tailwind + DaisyUI, `data-theme="dark"`. Cookie-auth с единственным админом из `appsettings`.
    - [ ] Self-observability — CLEF-клиент к YobaLog, выделенный workspace `yobaconf-ops` (spec §11). Рекурсия невозможна по конструкции (YobaLog не знает про YobaConf).

- [ ] **Фаза B — редактирование.** CRUD по нодам в UI с Monaco, audit log (immutable history), soft delete, diff текущей версии с предыдущей.
- [ ] **Фаза C — секреты.** Variables & Secrets с AES-256, маскирование в UI (`******`), отдельный scope ключей для доступа к секретам. Мастер-ключ — env var, не в БД/конфиге.
- [ ] **Фаза D — клиентские SDK.** .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun.
- [ ] **Фаза E — push для сверхнагрузок.** Экспорт собранного JSON в Redis/Consul/S3.

## Тестовое покрытие до фазы A

- [~] **Резолвинг (snapshot tests):** пока нет JSON-сериализатора — первые тесты сделаны на типизированных accessors (`GetString`/`GetInt`). Snapshot-движок подключится, когда появится сериализатор HOCON→JSON.
- [x] **Fallthrough:** `FallthroughTests.cs` — exact hit, missing leaf → ancestor, missing subtree → root-level, no ancestor → null, ancestor chain root-to-leaf ordering, skipped middle nodes. 6 тестов.
- [x] **`NodePath` валидация slug:** `NodePathTests.cs` — валидные сегменты, round-trip между `.`- и `/`-нотацией, отклонение прописных букв / underscore / ведущий-тире / коротких сегментов / пробелов / точек внутри сегмента, `$`-системный префикс, `Parent` walk, `Root == default`, value-equality. 15 тестов.
- [x] **HOCON merge smoke** (Phase A.1 follow-up): `HoconMergeTests.cs` — `.WithFallback` скаляры, deep objects, substitution через concat-before-parse, optional `${?var}`, required substitution бросает at parse-time. 5 тестов.
- [ ] **Циклические инклуды:** `A → B → A` ловится с понятной ошибкой; ограничение глубины.
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

- [ ] **Циклические инклуды на уровне движка:** защита от `A → B → A` с понятной ошибкой; ограничение глубины. (Частично проверяется тестом в pre-Phase-A чеклисте; полная реализация — в Phase A после Hocon-гейта.)
- [x] **Мастер-ключ AES** — переменная окружения, пробрасываемая через CI (GitHub Actions secret). Зафиксировано в `spec.md` §2.
- [x] **Лог аудита** — immutable history, хранится всегда. Зафиксировано в `spec.md` §7.
- [ ] **Клиентские SDK** — обёртки для .NET (`IConfigurationProvider`), Python (Pydantic source), TS/Bun. Phase D.
