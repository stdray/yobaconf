# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

## 2026-04-21 — OpenTelemetry self-emission: gated on yobalog Phase F

**Решение:** YobaConf получает **только** self-emission (эмитит OTel-spans своего `ResolvePipeline` + HTTP endpoint'ов + SQLite writes). OTLP-ingestion **не делаем** — YobaConf не log-store, этому не место. Metrics — тоже non-goal (территория Prometheus/Grafana). Реализуется как Phase C.5 между Phase C (secrets) и Phase D (client SDKs). Gate через `OpenTelemetry:Enabled` в `appsettings.json` (default `false` для pet-scale — включается когда реально нужно трассировать).

**Сancerовая зависимость:** Phase C.5 **gated на yobalog Phase F completing** — до тех пор, пока у yobalog нет OTLP-endpoint'а, экспортировать некуда (точнее, экспортировать **можно**, но в сторонний collector — Seq 2023.2+, Jaeger, Tempo — что противоречит стратегии "всё в yobalog"). Синхронизация с yobalog: их Phase F проектируется с расчётом на то, что YobaConf будет первым dog-food клиентом через C.5. Они дают endpoint, мы даём им трафик — взаимно полезный порядок.

**Причина пересмотра priority'а** (ранее в drafts было "self-emission value-gated, может ждать бесконечно"):
- Реальность stack'а: ты — единственный user, у которого **5 реальных consumer'ов** yobaconf/yobalog (`KpVotes`, `yobapub`, `animemov-bot-cs`, `yobaspeach`, сам YobaConf). Это значит — **distributed tracing через 5 сервисов** = near-term must-have, не "nice-to-have".
- Concrete use case: клиент шлёт команду в `animemov-bot`, тот дёргает YobaConf за конфигом, YobaConf фетчит из SQLite. Сейчас это 3 отдельных лог-потока — корреляция мануальная по timestamp'ам. С end-to-end trace'ами — одна waterfall-диаграмма в yobalog Phase H.
- YobaConf **особенно** выигрывает от span-ов: pipeline его состоит из множества distinct стадий (fallthrough / variables / includes / parse / serialize), и текущая модель отладки ("добавил Console.WriteLine, пересобрал, развернул") плохо масштабируется. Spans делают эту видимость бесплатной.

**Scope (что инструментируем):**
- `ResolvePipeline` — root span `resolve` с child-spans per stage: `fallthrough-lookup`, `variables-resolve`, `include-preprocess`, `hocon-parse`, `json-serialize`, `etag-compute`.
- HTTP endpoints (`/v1/conf/{path}`, `/health`, `/version`) — автоматически через `OpenTelemetry.Instrumentation.AspNetCore`.
- SQLite writes (`SqliteConfigStore.AppendAudit`, `FindNode`) — **вручную** через `ActivitySource.StartActivity`, потому что `OpenTelemetry.Instrumentation.Sqlite` не существует (ни official, ни community — см. yobalog decision-log 903fd4a). 10-15 строк на method-границе.
- IncludePreprocessor DFS — один root `include-preprocess` span с `yobaconf.includes.count` attribute; не эмитим child-spans per node (включения могут быть десятки → шум).

**Out of scope:**
- OTLP-ingestion (YobaConf ничего не ингестит кроме HTTP requests; это обрабатывается existing endpoint'ами).
- Metrics (counters / gauges / histograms) — `OpenTelemetry.Metrics` не подключаем. Если понадобится "rps / p99 latency" — запроси через yobalog KQL (`summarize count() by bin(@t, 1m)` по span-data).
- Logs через OTel Logging provider — уже есть CLEF-pipeline в yobalog (Phase 11 self-observability), не дублируем.
- `Instrumentation.HttpClient` — YobaConf не делает outbound HTTP (кроме self-observability в yobalog, и там хватает auto-AspNetCore со стороны yobalog).

**Технические решения:**
- **OTLP HTTP/Protobuf only**, не gRPC — зеркалит yobalog Phase F (их же reasoning: default в 2026, не требует HTTP/2 через прокси, меньше dep-bloat).
- **`OpenTelemetry.Proto` через NuGet**, не compile-from-source — зеркалит yobalog 903fd4a (proto-generated DTO = wire boundary exception, не escape от "max static typing" инварианта).
- **Endpoint configurable**: `OpenTelemetry:OtlpEndpoint` default `http://localhost:4318/v1/traces` — работает с Seq 2023.2+, Jaeger, Tempo, yobalog (после Phase F). Отдельный package — один конфиг, любой collector.
- **Service name**: `"yobaconf"` (default, override через `OpenTelemetry:ServiceName`). Стандартная OTel-конвенция (`service.name` resource attribute).
- **Test gate**: `AddOpenTelemetry()` skipped под `IsEnvironment("Testing")` — тот же паттерн что `UseHttpsRedirection` skipped для Kestrel-based integration тестов. Span-unit-тесты поднимают `ActivityListener` вручную.

**Rejected alternatives:**
- **Полный OTel-suite (tracing + metrics + logging).** Metrics duplicate Prometheus, logging duplicates CLEF-pipeline. Для yobaconf хочется единственно нового — tracing, т.к. его сейчас нет.
- **Ingest OTLP в yobaconf.** Не log-store. Нет смысла.
- **Ждать Phase F finalization в yobalog перед тем как добавлять C.5 в план.** План-bullet сам по себе не вредит даже если F задержится — C.5 просто ждёт в списке. Симметрия phasing'а между yobalog и yobaconf помогает онбордингу.
- **Делать Phase C.5 до Phase C (secrets).** Tracing over un-instrumented secret-path'а (C нет → secrets нет → pipeline без decrypt-стадии) даёт частичную картину. Лучше дождаться C — тогда `secrets-decrypt` span показывает ROI.

**Открытые вопросы (решаются когда дойдём до реализации):**
- Формат cross-service context propagation — W3C Trace Context (стандарт 2026) vs B3 (legacy). W3C по default в OpenTelemetry.NET — вероятно, просто берём.
- Sampling: 100% (pet-scale, всегда) или конфигурируемый ratio (`OpenTelemetry:Sampling:Ratio`)? Начнём с 100%, добавим sampling когда появится реальная нагрузка.
- Когда делать `OpenTelemetry:Enabled = true` by default? Вероятно — одновременно с переключением yobaconf на prod-окружение (когда первый из 5 consumer'ов реально упрётся во что-то debuggable через traces).

**Cross-refs:**
- yobalog decision-log 2026-04-21 "OpenTelemetry integration: scope + cost assessment" (commit 903fd4a) — корневое решение по всей OTel-стратегии стека.
- yobalog Phase F bullets в их plan.md — будут работающим endpoint'ом для C.5.
- spec §2 (будет обновлён при реализации — упоминание OpenTelemetry в target stack'е).

---

## 2026-04-21 — Drop jQuery from UI stack; htmx + Alpine.js + vanilla TS only

**Решение:** YobaConf фронт — htmx (server-driven HTML swaps) + Alpine.js (опциональный локальный state для модалок/toggle'ов) + собственный TS в `ts/admin.ts` (минимум). **jQuery не используется.** В spec §9 это теперь зафиксировано как negative invariant.

**Причина:**
- YobaConf UI-scope маленький: дерево путей + CodeMirror редактор (Phase B) + пара модалок (conflict resolution, import-paste). Ничего из этого не требует jQuery.
- 2026 фронт-ландшафт: jQuery на фазе legacy. Современные браузеры закрыли почти все use case'ы, ради которых jQuery был стандартом (`querySelector`, `fetch`, CSS animations, ES2022 syntax). Новое приложение на jQuery = сразу tech debt.
- htmx + Alpine покрывают 100% наших потребностей: htmx для server-side обновлений, Alpine для локальных UI-стейтов (modal open/close, tab switching, `x-show`/`x-data`). Alpine API проще jQuery — reactive declarations вместо императивных обработчиков.
- Bundle-размер: Alpine ~13 KB min, htmx ~14 KB min. jQuery был бы +30 KB для ничего.
- Соответствие с yobalog, который тоже без jQuery (собственный TS в `ts/admin.ts` + htmx). Два sibling-проекта = одна фронт-страна.

**Откатили:** первоначальную формулировку §9 "jQuery (для сложных UI-манипуляций) + Alpine.js (опционально)" — скопировано бездумно из какого-то референса. jQuery "для сложных манипуляций" в наш scope не попадает. Pre-emptive cleanup: удалим упоминание до того, как кто-то подключит `<script src="jquery-3.x.js">` в Layout.

**Не исключаем навсегда:** если когда-нибудь появится интеграция с legacy-библиотекой, которая требует jQuery (напр. старый datepicker), — пересмотрим с явным use case'ом в decision-log'е. Сейчас такого use case нет.

---

## 2026-04-21 — Master-key AES rotation: lazy re-encrypt on access (draft procedure)

**Решение (черновик для Phase C):** когда мастер-ключ AES ротируется:
1. Новый ключ попадает в env как `YOBACONF_MASTER_KEY_V2` (в дополнение к `YOBACONF_MASTER_KEY_V1`, который остаётся). Каждый ключ имеет свою `KeyVersion`-строку (напр. `"v1"`, `"v2"`).
2. Приложение на старте читает оба env var'а; держит их в `IKeyring` (сервис в DI) с lookup по `KeyVersion`.
3. При **чтении** Secret — смотрим `KeyVersion` записи, берём из keyring'а нужный ключ, расшифровываем.
4. При **записи** (создание новой записи или обычный UPDATE через UI) — всегда используем **текущий** ключ (`v2` после ротации). `KeyVersion` записи становится `"v2"`.
5. **Lazy re-encrypt:** старые записи с `KeyVersion = "v1"` остаются под старым ключом до их следующего UPDATE. Не перекодируем proактивно — даёт ротацию без downtime, без миграционных скриптов.
6. Когда все активные секреты имеют `KeyVersion = "v2"` (можно проверить SQL: `SELECT COUNT(*) FROM Secrets WHERE KeyVersion = 'v1' AND IsDeleted = 0` — 0), старый env var удаляется из деплоя. Keyring остаётся с одним ключом; AuditLog-записи под старым ключом становятся недоступны без расшифровки (это acceptable — история >6 месяцев редко нужна, и если нужна — можно временно поднять старый ключ в env).

**Почему lazy, не eager:** proactive re-encrypt job требует простой (читаем-пишем все секреты в одну транзакцию) или runtime координации (иначе midway-rotation читатель увидит одну запись старую, другую новую и запутается, если кеширует keyring). Lazy = zero-downtime, ценой "old key must live until last update on it" — но это стандартный trade-off envelope encryption.

**Открытые вопросы для имплементации в Phase C:**
- Форма команды "force re-encrypt all secrets now" в админке — для случая когда нужно принудительно погасить старый ключ.
- Алерт в self-observability: "N секретов под `KeyVersion = vX`, старый ключ в env всё ещё нужен" — чтобы не забыть почистить.
- Формат `KeyVersion` — строка? timestamp? sequence? Кандидат: `"vYYYY-MM"` (привязка к месяцу ротации) для человекочитаемости.

**Откатили:** идею "eager re-encrypt job" для простоты. YAGNI пока не появится конкретный compliance-сценарий.

---

## 2026-04-21 — Import converters: JSON / YAML / .env → HOCON

**Решение:** В Phase A добавляется UI-форма "New node from paste" — пользователь вставляет существующий конфиг в одном из трёх форматов, получает HOCON. Три независимых чистых функции в Core: `JsonToHocon(string) -> string`, `YamlToHocon(string) -> string`, `DotenvToHocon(string) -> string`.

**Поддерживаемые форматы в MVP:**
- **JSON → HOCON: no-op.** JSON — синтаксический subset HOCON. Любой валидный JSON текст уже парсится HOCON-движком as-is. Конвертер = валидация (`JsonDocument.Parse`) + pretty-print (опционально, для нормализации форматирования). Нулевая потеря семантики.
- **`.env` → HOCON: ручной парсер (~50 строк).** Построчный разбор `KEY=value` с учётом: `#`-комментариев, quoted/unquoted values (одинарные, двойные, без кавычек), escape-последовательностей в double-quotes (`\n`, `\t`, `\\`, `\"`), пустых строк. Ключи валидируются на HOCON-совместимость (буквы/цифры/подчёркивания; если встречаются точки — warn'им, т.к. HOCON интерпретирует как nested path).
- **YAML → HOCON: `YamlDotNet`.** Стандартная .NET-библиотека для YAML. Парсим в `YamlNode`-tree → walker в HOCON-текст. Anchors и aliases (`&anchor` / `*ref`) **разворачиваются** в конвертации (не сохраняем, HOCON этого не умеет чисто). YAML-теги (`!!int`, `!!str`) игнорируются — HOCON-парсер сам определит тип по литералу.

**Что не поддерживается в MVP:**
- **TOML → HOCON:** отдельный парсер, редкий case для pet-проектов. Добавим если user-ы попросят.
- **HCL → HOCON:** HashiCorp-specific; их экосистема не пересекается с нашей целевой.
- **Properties (Java-style) → HOCON:** встроенный формат HOCON — сам парсер Hocon 2.0.4 умеет читать `.properties`. Можно прикрутить напрямую через `HoconParser.Parse` с `.properties`-source-type, не делать отдельный конвертер.
- **Сохранение комментариев** при YAML → HOCON: YamlDotNet `DocumentStream`-mode имеет доступ к comment tokens, но их эмиссия в HOCON добавляет complexity без большого value (пользователь потом редактирует текст, перепишет комментарии сам).
- **Reverse direction (HOCON → YAML/JSON/env):** HOCON → JSON уже есть (serializer в Phase A). HOCON → YAML / HOCON → `.env` — не нужно: HOCON is our edit format, экспорт в другие форматы добавит сурпризы (`.env` не поддерживает nested objects, YAML добавит типов, которые HOCON не имел).

**Почему три независимых функции, а не общий IR:**
- Каждый формат имеет свои особенности: YAML multi-document streams, dotenv variable expansion (`${OTHER_VAR}`), JSON strict-primitives.
- Маппинг "всё в `Dictionary<string, object>`" теряет информацию в обратную сторону (например, YAML-число `1.0` vs string `"1.0"` — в словаре оба становятся double, рендер в HOCON неоднозначен).
- Три функции = 3 × ~50-150 строк кода + независимые тесты. Общий IR = шаред-код + 3 набора edge-case тестов всё равно.

**Use case (почему добавили к Phase A, а не в Phase B CRUD):**
- Phase A = "dog-food ready" означает "yobaconf хостит свои конфиги". Кто-то должен залить первые ноды. Ручное переписывание `.env` в HOCON = тренировочный барьер.
- Форма "paste & convert" намного проще, чем полный Monaco/CodeMirror editor. Textarea + format dropdown + Convert button + preview — 1 страница, zero JS-зависимостей (конвертация server-side через htmx).
- Преимущество отдельной страницы "New from paste" vs "create blank + paste в Monaco": пользователь мгновенно видит, что HOCON-формат получился валидный (preview + подсветка), до того как сохраняет.

**Откатили:** "один универсальный FormatConverter через общий IR". См. выше — не окупает сложность обратного маппинга.

---

## 2026-04-21 — CodeMirror 6 + Prism вместо Monaco Editor

**Решение:** HOCON-редактор в админке — **CodeMirror 6** (Phase B, editing); для read-only подсветки в дереве + JSON-preview — **Prism.js** (Phase A). Monaco Editor, который был в первоначальной спеке §5, **не берём**. Diff-view поверх CodeMirror через `@codemirror/merge` addon. HOCON-грамматика — ручной порт TextMate grammar из [sabieber/vscode-hocon](https://github.com/sabieber/vscode-hocon) в две формы: Prism component (~80 строк regex) и CodeMirror `StreamLanguage` tokenizer (~150 строк).

**Причина:**
- **Bundle size** — Monaco 3-5 MB (editor core + language services + web workers) vs CodeMirror 6 ~200-400 KB + Prism ~25 KB. Для admin-app, куда пользователь заходит редко, 3-5 MB первой загрузки — заметная задержка без выигрыша в функциональности, которая нам нужна.
- **Фичи, которые реально используем** — syntax highlighting, basic indent, find/replace, diff view. Весь этот набор у CodeMirror есть из коробки, у Prism — highlight-only (достаточно для read-only). Monaco-шная multi-cursor / command palette / breadcrumbs — не нужны для HOCON-файлов <500 строк.
- **HOCON grammar эффорт эквивалентен.** Monaco использует TextMate grammars, реюзнем sabieber'скую as-is. CodeMirror — нужен ручной порт в StreamLanguage. Порт один раз, ~150 строк. Компенсирует отсутствие "бесплатного" реюза, учитывая выигрыш в размере.
- **Setup complexity.** Monaco требует `MonacoEnvironment.getWorkerUrl`, AMD-legacy артефакты, worker-based architecture — для bun-сборки всё это решаемо, но с доп. скриптами + документации. CodeMirror 6 — ESM-native, web workers опциональны для базового highlighting. Prism — один `<script>` плюс конфиг.
- **Ecosystem alignment со stdray.Obsidian.** Obsidian (на котором основан `ConflictSolverService` для three-way merge в conflict-resolution UI) использует CodeMirror 6. Общая экосистема → знакомые паттерны → меньше trial-and-error.

**Deployment phasing:**
- **Phase A (read-only UI):** Prism + HOCON-component. Нет editing, есть подсветка `RawContent` в дереве и JSON-preview (Prism JSON grammar из коробки). ~25 KB на бандл.
- **Phase B (CRUD editing):** CodeMirror 6 + StreamLanguage HOCON-tokenizer. Добавляется ~300 KB (editor core + extensions + HOCON). Diff-view через `@codemirror/merge` — conflict modal (spec §7) поверх него.
- **Отложено до явного use-case'а:** Lezer HOCON grammar (полноценный AST-parser) — упрощает семантические фичи типа "go to definition" по `include`-targets или autocomplete по `${var}`-ссылкам. Пока не нужно, StreamLanguage закрывает highlighting + indent.

**Rejected alternatives:**
- **Monaco сразу** — 10× bundle при том же наборе использованных фич. TextMate-grammar-reuse как аргумент перестаёт играть когда грамматика — ручной порт в любом случае (sabieber'ская не покрывает всех edge cases HOCON substitutions/includes).
- **Plain textarea + Prism overlay** (как yobalog для KQL) — для HOCON с includes и длинными блоками UX плохой: нет folding, find/replace только через браузер, нет bracket-matching. KQL у yobalog короткий (~50 chars), HOCON-файлы сотни строк.
- **Ace Editor** — промежуточный по весу (~500 KB-1MB), но менее активно развивается чем CodeMirror 6; ecosystem переключается на CM6.

**Источники (для истории обсуждения):**
- [Prism.js supported languages](https://prismjs.com/) — HOCON not in list, custom component needed.
- [sabieber/vscode-hocon](https://github.com/sabieber/vscode-hocon) — TextMate grammar base, MIT.
- [CodeMirror 6 Lezer docs](https://lezer.codemirror.net/docs/ref/) — grammar format if we ever upgrade from StreamLanguage.

**Откатили:** Monaco Editor из первоначальной §5 спеки ("Monaco оправдан, потому что HOCON-файлы бывают длинными"). Аргумент "длинные HOCON-файлы" не тянет: и CodeMirror, и Prism отлично обрабатывают 10k+ строк. Длина сама по себе не оправдывает 10× bundle.

---

## 2026-04-21 — Build pipeline: Cake + GitVersion; Docker = chiseled + smoke-test; deploy = manual `deploy` tag

**Решение:** сборочный pipeline одинаков для yobaconf и yobalog (будут синхронно расширяться):
- **GitVersion** (`GitVersion.yml`, `next-version: 0.1.0`) — ContinuousDelivery mode, конфиг скопирован с yobapub. Версия прокидывается в MSBuild `Version` / `InformationalVersion` + в Docker build-args (`APP_VERSION`, `GIT_SHORT_SHA`, `GIT_COMMIT_DATE`).
- **Cake** (`build.cake` + `build.sh` + `build.ps1` + `.config/dotnet-tools.json`) — orchestration в C# DSL. Tasks: Clean → Restore → Version → Build → Test → Docker → DockerSmoke → DockerPush. Локально `./build.sh --target=Test` даёт полный цикл; `--target=Docker` добавляет сборку образа; `--target=DockerPush --dockerPush=true` пушит в ghcr.io.
- **Docker runtime** — `mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled`. ~15MB base, нет shell. Self-contained publish `linux-x64`. Dockerfile двухстадийный: SDK + bun installer (для MSBuild BuildFrontend target) → chiseled runtime. Риск "chiseled упал на runtime, не попасть внутрь" закрывается **DockerSmoke** task: `docker run -d` + `curl /` с 30s-timeout. Если не отвечает — CI красный, push не происходит.
- **Deploy** — **только по ручному тегу `deploy`** (`git tag deploy && git push origin deploy`). Main-push: build + test + Docker build + push в ghcr.io, но **без** SSH-деплоя. Pattern повторяет yobapub `tags: ['apps']`, но с тегом `deploy` для явной семантики.

**Причина:**
- **GitVersion vs ручные теги:** ручное тегирование = каждый раз помнить какая версия; GitVersion даёт детерминированный semver из истории commits+branches. Ноль cognitive overhead после setup.
- **Cake vs голый shell в ci.yml:** см. yobapub-овский 170-строчный ci.yml. Cake-based — ~30 строк ci.yml + 150 строк build.cake, зато локально `./build.sh --target=X` полный parity с CI. При трёх orchestration-шагах (GitVersion → build → Docker с build-args) Cake уже окупается.
- **chiseled vs обычный ubuntu:** 15MB vs 200MB, меньше attack surface, MS best-practice для .NET 8+. Единственный минус ("нет shell для дебага") закрывается smoke-test'ом в CI.
- **Deploy по тегу, не по push main:** main-merge должен быть безопасен (build + test + push в registry), но не ронять прод. Тег `deploy` = явный act of will.

**Версии инструментов:** Cake.Tool 5.0.0, GitVersion.Tool 6.4.0, Cake.Docker 1.3.0. Cake 5.0 свежее animemov-bot-cs (там 4.0.0), но greenfield — смысла оставаться на старом major нет. GitVersion 6.4 синхронно с yobapub `gittools/actions@v4`.

**Отклонения от animemov-bot-cs reference:**
- Dockerfile включает bun installer в build stage (animemov'у не нужно — нет фронта).
- Cake task `DockerSmoke` между `Docker` и `DockerPush` (animemov не HTTP-серверный).

**Откатили:**
- "Голый shell в ci.yml без Cake" — ci.yml разрастётся до yobapub-объёма, local/CI parity теряется.
- "Обычный ubuntu runtime для простоты" — выигрыш в debug-UX нулевой (всё равно смотрим логи), проигрыш в размере 10×.
- "Deploy на каждый main push" — риск ронять прод без act of will.

---

## 2026-04-21 — SQLite + linq2db вместо LiteDB

**Решение:** YobaConf хранит данные в SQLite через `linq2db.SQLite.MS` (та же версия, что в yobalog). Single `.db` file, WAL mode. Миграции — через linq2db (позже) или руками на старте Phase A (схема = 4 таблицы: Nodes, Variables, Secrets, ApiKeys + AuditLog). Транзитивная CVE на `System.Drawing.Common 4.7.0` уже запинена forward через CPM — ничего дополнительно не ломается.

**Причина:**
- Модель yobaconf rigid и row-shaped: Nodes/Variables/Secrets/ApiKeys/AuditLog — плоские строки с фиксированными полями. NoSQL-гибкость LiteDB здесь не используется — LiteDB по факту играет роль row-store через document-API.
- Синхронно с yobalog: тот же стек (`linq2db.SQLite.MS 5.4.1`), те же паттерны миграций, общий tooling опыт (sqlite CLI, DB Browser for SQLite, Rider viewers).
- AuditLog insert-heavy и растёт вечно — SQLite VACUUM / WAL checkpointing / бэкап-story хорошо документированы; LiteDB здесь слабее.
- FTS5 на будущее: "найти все ноды, ссылающиеся на `${db_host}`" — одна строка в схеме, если вдруг понадобится.

**Откатили:** LiteDB как выбор из изначальной спеки. Причина отката — rigid-data не использует гибкость document-store, а yobalog уже sqlite-based: расхождение БД-стеков = tax на каждом bump'е.

---

## 2026-04-21 — Без workspaces в MVP: иерархические пути сами по себе namespace

**Решение:** YobaConf не вводит понятие workspace поверх path-tree. Дерево путей — единственный namespace. Изоляция между проектами/командами — через API-ключи с `RootPath` на нужное поддерево.

**Причина:**
- Path-дерево уже даёт всё, что даёт workspace в yobalog: per-tenant изоляция через scoped ключи, independent config trees. Добавлять ещё один уровень (workspace/path) = избыточная вложенность при тех же capabilities.
- YAGNI: yobaconf стартует single-user / self-hosted. Multi-tenant истории нет на горизонте. Если когда-нибудь возникнет — миграция тривиальна: все существующие пути префиксятся workspace-id'ом, старые API-ключи rewritе'ятся автоматически.
- Меньше API surface: `/v1/conf/{path}` vs `/v1/conf/{workspace}/{path}`; UI-дерево проще.

**Откатили:** соблазн скопировать yobalog-структуру 1-в-1 ("workspace поверх всего") для симметрии между проектами. Симметрия кажущаяся: в yobalog workspace нужен, потому что логи разных проектов требуют физической изоляции (разные `.db`, разные retention). В yobaconf логическое разделение через paths + scoped keys работает без физической раздельности.

---

## 2026-04-21 — Variables и Secrets — отдельные таблицы, не общий column с `IsSecret`-флагом

**Решение:** `Variables (Key, Value, ScopePath, ContentHash)` и `Secrets (Key, EncryptedValue, Iv, AuthTag, KeyVersion, ScopePath, ContentHash)` — разные таблицы в SQLite. В API §4 pipeline они унифицированы в общий `ResolvedVariable` список для HOCON-движка, но хранение и audit-пути раздельные.

**Причина:**
- Type safety: секрет в единой таблице с `IsSecret`-флагом — семантическая мина. Одна забытая ветка `if (row.IsSecret) Decrypt(...)` = plaintext-утечка. Разные таблицы = тип на уровне DAO физически разделяет code paths.
- Schema физически разная: `Value TEXT` vs `EncryptedValue BLOB + Iv BLOB + AuthTag BLOB + KeyVersion TEXT`.
- AuditLog-инвариант "секреты в аудите ВСЕГДА encrypted" (§7) — проще enforce'ить через отдельную `AuditLog.Kind='secret'` ветку с blob-типизацией, чем условной шифровкой в общей ветке.
- UI: Variables показываются plaintext, Secrets — `******` + reveal. Разные view-слои, разные data-testid, разные permission-checks (в будущем).

**Откатили:** single-table + flag (проще в схеме, экономит JOIN). Type safety важнее минимализма при 2 таблицах — схема всё равно тривиальна.

---

## 2026-04-21 — Include-семантика финализирована: каждый `.hocon` = отдельная нода, scope `dir(target) ancestor-or-equal dir(including)`, циклы runtime-детектятся

**Решение (финальное, после уточнения модели):** в ментальной модели каждый `.hocon`-файл становится отдельной нодой на пути `dir-segments/filename-without-ext`. Директории сами по себе нодами не являются, если в них не создан физический `RawContent`. Правило `include "absolute-path"`:
- `dir(target)` (target path без последнего сегмента) должен быть **ancestor-or-equal** `dir(including-node)`.
- Это разрешает: ancestors (любые предки в директорном смысле), siblings в той же директории (`project-a/test/service1` включает `project-a/test/service2`).
- Запрещает: descendants, sibling-субдиректории (`project-a/test/*` не видит `project-a/dev/*`), self-include.
- Относительные пути (`../foo`) в MVP не поддерживаются — только абсолютные от корня.
- **Циклы возможны** (мутуальные sibling-includes) — runtime detection через DFS с `HashSet<NodePath> visited`; нарушение → `CyclicIncludeException` с цепочкой путей.

Резолвинг — **preprocess-стадия перед HOCON parse**, не через `ConfigResolver` callback пакета Hocon: native callback не получает контекст "кто включает", из-за чего scope-валидация и cycle-detection на его API не выражаются чисто. Своя DFS-обёртка раскрывает все `include`-директивы в плоский HOCON-текст, затем `ParseString` на плоском тексте.

**Причина текущей формулировки (после возражений пользователя):**
- Первое решение (auto ancestor-merge, без explicit include): отвергнуто — неявное поведение, автор не может опт-аутнуться.
- Второе решение (explicit include только ancestors-in-path): отвергнуто — реальная ментальная модель пользователя = filesystem-like с несколькими файлами в директории, где service1 и service2 как siblings должны мочь включать друг друга. Строгое "ancestors-only" (где sibling-include запрещён) ломает этот паттерн.
- Третье (финальное): правило "dir ancestor-or-equal" разрешает siblings в той же директории — естественно для filesystem-конвенции — и закрывает use case. Цена: runtime cycle detection, но это стандартная задача.

**Rejected alternatives:**
- **"Virtual env-view" (неявное `/project-a/test/service1` как merge `/project-a/service1` с variables из `/project-a/test/`):** отложено. Паттерн элегантен для env-overlay, но вводит implicit routing rule ("последний сегмент URL ищется в ancestor-директориях"), противоречащий принципу явности. Тот же result выражается explicit-нодами: `project-a/service1-base` + `project-a/test/service1` с `include "project-a/service1-base"`. Больше букв, меньше магии. Если use case "одна и та же конфигурация для 10 окружений с минимальными различиями" станет felt pain — добавим как отдельную template-фичу без перемоделирования routing.

**Откатили:**
- "Include-семантика: только auto ancestor-merge, explicit отложен" (раньше сегодня) → пользователь обосновал явный pull-model.
- "Explicit include только для proper ancestors" (тоже сегодня) → неверная модель дерева: sibling-includes нужны для filesystem-подобной организации.

**Источники (для истории):** обсуждение 2026-04-21 в чате по архитектуре, конкретно про пример с `project-a/logger.hocon`, `service1-base.hocon`, `service2-base.hocon` и потенциальные циклы между service1.hocon ↔ service2.hocon.

---

## 2026-04-21 — Optimistic locking при редактировании через `ContentHash` column

**Решение:** `Nodes`, `Variables`, `Secrets` имеют column `ContentHash` (sha256 hex от `RawContent` / `Value` / `EncryptedValue` соответственно). UI-редактор при save шлёт `expectedHash` вместе с новым значением; серверный UPDATE: `UPDATE ... SET ... WHERE Id = @id AND ContentHash = @expected`. Если rows affected = 0 → конфликт (параллельное редактирование), UI показывает three-way diff модалку (inspired by stdray.Obsidian ConflictSolverService — см. `D:\prj\github\stdray\stdray.Obsidian\stdray.Obsidian\ConflictResolution\ConflictSolverService.cs`).

**Причина:**
- Pessimistic lock (transaction/lease held during edit) плохо масштабируется и ломает UX: пользователь закрыл таб без "cancel" → lease застой → нужен cleanup job.
- sha256 уже вычисляется для ETag (§4.6); сохранить в row — бесплатно.
- User flow "во время твоего редактирования кто-то поменял файл — хочешь смержить автоматически или вручную?" — знаком по Obsidian-плагину автора, UX уже продуман.

**Откатили:** pessimistic lock. Three-way merge UI для конфликтов — не отдельный продукт, а переиспользование существующего ConflictSolverService-паттерна.

---

## 2026-04-21 — Hocon 2.0.4 резолвит substitutions at parse-time, а не после merge — §4.5 подстраивается под это

**Решение:** первые unit-тесты показали, что `HoconConfigurationFactory.ParseString(text)` вызывает resolve substitutions прямо внутри парсера. Required `${var}`, у которой нет значения в **том же тексте**, бросает `HoconParserException: Unresolved substitution` — `.WithFallback(...)` поверх уже спарсенных Config'ов substitution не докидывает. Из этого следуют два работающих паттерна inject переменных, которые §4.5 обязан использовать:

1. **Склейка текстов до parse.** Variables рендерятся в HOCON-строку, конкатенируются с пользовательским HOCON, парсится единожды. Substitution резолвится сразу. Пример: `ParseString(varsText + "\n" + userText)`.
2. **Optional substitution (`${?var}`) в пользовательских конфигах.** Парсится чисто, даже если переменной нет; merge через `.WithFallback` с другим config'ом тоже не подставит — `?`-substitution живёт только внутри одного парса.

Оба паттерна покрыты тестами в `HoconMergeTests.cs` (`Substitution_ResolvesWhenVariablesAreConcatenatedBeforeParse`, `OptionalSubstitution_LeavesPreviousValueWhenVariableMissing`). Третий тест (`RequiredSubstitution_InWithFallbackChain_ThrowsAtParseTime`) явно фиксирует ограничение, чтобы оно не было забыто.

**Следствие для §4 pipeline:**
- Конвейер собирает финальный HOCON-текст склейкой: (1) рендерит variables → HOCON, (2) прикладывает родительские ноды в порядке root → leaf (через ту же склейку с помощью `WithFallback`-семантики = более поздние перебивают ранних), (3) парсит единожды.
- `.WithFallback(parsedConfig)` используется там, где тексты по архитектуре принадлежат разным источникам (например, два HoconRoot'а от разных нод без общих substitutions) — т.е. merge объектов остаётся полезным для чистого mergе, но не для inject переменных.
- План A детализирован: резолвинг переменных (§4.5) происходит **до** парсинга, не после, а `.WithFallback` участвует только в мердже родительских нод (§4.4).

**Причина:** не наш баг, а документированное поведение akkadotnet/HOCON 2.0.4 — в их же примере `src/examples/Fallback/Program.cs` substitution помечен `${?ENV_MY_PROPERTY}` (optional), т.е. авторы знают про ограничение и обходят его именно так.

**Откатили:** предположение из моего первого прохода по спеке ("parse → merge → inject → Resolve") — на этом порту HOCON последний Resolve-шаг не существует, substitution резолвится at parse-time.

---

## 2026-04-21 — HOCON-гейт пройден: берём `Hocon 2.0.4` (akkadotnet/HOCON) для Phase A

**Решение:** HOCON-парсер = пакет [`Hocon`](https://www.nuget.org/packages/Hocon/2.0.4) от команды Akka.NET (репо [akkadotnet/HOCON](https://github.com/akkadotnet/HOCON)). Все критичные для §4 pipeline API присутствуют:
- `HoconConfigurationFactory.ParseString(string)` — парсинг из строки (для чтения `RawContent` из LiteDB).
- `HoconParser.Parse(hocon, ConfigResolver)` — парсинг с callback'ом на `include` (типы: `File`, `Resource`, `Url`). Это ровно тот хук, через который §4.3 Fallthrough+родители будет доставать include-фрагменты из LiteDB вместо файловой системы.
- `.WithFallback(other)` — каскад для §4.4 сборки из дерева родителей.
- `${?var}` / `${var}` substitution resolver встроен; inject переменных — через `ParseString(varsHoconText).WithFallback(userConfig)` (классический Lightbend-паттерн), либо через тот же `ConfigResolver` callback.
- `.PrettyPrint(indent)` / `.ToString()` — сериализация результата обратно в HOCON/JSON-подобную форму для §4.6 ответа.

**Причина:**
- Все API в примерах `src/examples/{Fallback,ExternalIncludes,HelloHocon}` живьём показывают нужные нам сценарии — не пришлось делать свой spike.
- Apache-2.0, стабилен (порт под Akka.NET, поддерживается их core-командой).
- Пакет на NuGet таргетит `netstandard1.3 + net461` (релиз октябрь 2021), но netstandard1.3 — subset of netstandard2.0, .NET 10 их тянет через compat shim. Проверка в Phase A.0 smoke-test подтвердит, что shim'ы не ломают.

**Риски (зафиксировать, не блокеры):**
- Низкая активность: последние коммиты в `dev` (2025-04, 2025-06) — только dep bumps (Microsoft.SourceLink и FAKE script removal). Последний функциональный релиз — 2.0.4 (октябрь 2021). На `dev` ветке уже netstandard2.0, но релиза не было 3+ года.
- Downgrade path: если пакет сломается под .NET 10 — форкнуть репо (Apache-2.0 разрешает), самим выпустить `YobaConf.Hocon` как internal NuGet. API стабилен, патчить придётся редко.
- Явно **не берём**: `Hocon.Extensions.Configuration` (лишняя обёртка под `IConfiguration`, нам не нужна — мы сами генерим JSON), `Hocon.Immutable` (отдельный tree-model, в §4 не требуется), `Akka.Hocon` (часть Akka-дистрибутива, тянет лишнее).

**Откатили:**
- Идею писать свой HOCON-парсер — не оправдано до первого серьёзного падения пакета.
- Смену формата на TOML/YAML — §1 спеки прямо требует HOCON за счёт `include` + substitution (TOML не поддерживает include, YAML substitution — через anchors, не по именам).

**Источники:**
- [NuGet: Hocon 2.0.4](https://www.nuget.org/packages/Hocon/2.0.4)
- [GitHub: akkadotnet/HOCON](https://github.com/akkadotnet/HOCON)
- Примеры: [`src/examples/Fallback`](https://github.com/akkadotnet/HOCON/tree/dev/src/examples/Fallback), [`src/examples/ExternalIncludes`](https://github.com/akkadotnet/HOCON/tree/dev/src/examples/ExternalIncludes)

---

## 2026-04-21 — Документация вынесена из монолитного `yobaconf.md` в `doc/{spec,plan,decision-log}.md`

**Решение:** следуем структуре yobalog: `doc/spec.md` — чистая спека без прогресса, `doc/plan.md` — фазы + тест-чеклист + инварианты, `doc/decision-log.md` — решения с датами, новые сверху. Корневой `yobaconf.md` остаётся как исходник на время миграции; удалится после первого коммита.

**Причина:** в yobalog split прижился — легко править спеку, не задевая прогресс, и наоборот. Один файл-на-всё быстро превращается в свалку, где неясно, что утверждение (спека), что задача (план), что зафиксированное решение (лог). Зеркальная структура двух проектов = разработчик не переключает контекст между репозиториями.

**Откатили:** идею "один `README.md` — и спека, и план" (как было в исходном `yobaconf.md`).

---

## 2026-04-21 — Репо-гигиена и тулинг зеркально из yobalog

**Решение:** `.gitignore`, `.gitattributes`, `.editorconfig`, `global.json`, `Directory.Build.props`, `Directory.Packages.props` — скопированы из yobalog с минимальными правками (убран Kusto/linq2db/KustoLoco из Packages.props; добавлены HOCON/LiteDB после Phase A.1). Стиль кода (табы ширины 2, immutability-first, expression-bodied, omit implicit modifiers, strict TS, `data-testid`-only UI-селекторы) перенесён в AGENTS.md один-в-один.

**Причина:** оба проекта на .NET 10 + bun + Tailwind + Razor SSR, один и тот же автор/команда. Разошедшиеся `.editorconfig` / `Directory.Build.props` = две копии одних и тех же правок при каждом bump SDK. Единый стиль = меньше переключения контекста.

**Откатили:** ничего — старта не было, прижилось с нуля.

---

## 2026-04-21 — Phase A.1 (HOCON-гейт) вынесен перед Phase A

**Решение:** до начала Phase A провести blocking spike-проверку HOCON-парсера для .NET: распарсить с `include`, сделать `.WithFallback`, программно inject переменную, позвать `.Resolve()`, получить JSON. Если любой шаг требует обходов — фиксируем в этом логе и выбираем альтернативу (Akka.NET hocon-cs, свой парсер, смена формата).

**Причина:** весь pipeline §4 в спеке завязан на предположение "HOCON-движок умеет программно принимать переменные и делать resolve". Если выбранный пакет не поддерживает inject (а только parse-from-string), §4.5 резолвинг превращается в pre-processing текста — это другая архитектура. Дешевле проверить до того, как весь Core написан под старое предположение.

**Откатили:** прямое "берём Hocon.Net и пишем Phase A" без валидации — риск переписывать Core на втором спринте.

---

## 2026-04-21 — Единственное направление зависимости: YobaConf → YobaLog

**Решение:** YobaConf пишет свои события в YobaLog через CLEF endpoint (self-observability, spec §11). YobaLog не зависит от YobaConf — в YobaLog `spec.md` §1 зафиксировано, что его конфиг приходит только из `appsettings.json`. API-ключ YobaConf → YobaLog хранится в `appsettings.json` самого YobaConf, **не** в YobaConf-ноде.

**Причина:** если YobaLog начнёт читать конфиг из YobaConf, получим цикл: YobaConf при старте пишет событие в YobaLog → YobaLog хочет узнать retention-policy → идёт в YobaConf → YobaConf ещё не поднят. Разрыв цикла — на стороне YobaLog: его контракт уже исключает зависимость от YobaConf.

**Откатили:** соблазнительную идею "централизовать всё через YobaConf, включая сам YobaLog" — работает только до первого перезапуска.

---

## 2026-04-21 — `appsettings.json` для bootstrap YobaConf, мастер-AES-ключ — env var

**Решение:** YobaConf **не** конфигурируется через YobaConf. Инфраструктура (admin seed, HTTPS, пути к БД, API-ключ на YobaLog) — `appsettings.json`. Мастер-ключ AES для расшифровки секретов — переменная окружения `YOBACONF_MASTER_KEY`, пробрасывается через CI (GitHub Actions secret) / docker env. Не в `appsettings`, не в БД.

**Причина:**
- Self-config = бутстрап-цикл: чтобы прочитать свою ноду `$system/yobaconf`, нужно уже иметь мастер-ключ для расшифровки секретов в ней — его и хотели достать.
- Мастер-ключ в `appsettings.json` = попал в git на первом же коммите через невнимательность. Env var проходит через secret-менеджмент CI/хостинга (изолировано от репозитория).

**Откатили:** идею "секции `YobaConf:Master` в `appsettings.json` под ключ" — слишком близко к коду и git'у.

---

## 2026-04-21 — Slug-regex для имён нод: `^[a-z0-9][a-z0-9-]{1,39}$` (синхронно с workspace ID в YobaLog)

**Решение:** имена нод — slug в стиле Docker image names. Точка запрещена внутри имени (разделитель пути в API). Префикс `$` зарезервирован для системных (`$system`, `$bootstrap`). Тот же regex используется для workspace ID в YobaLog.

**Причина:**
- Одинаковая валидация в двух связанных проектах = один утилитарный кусок кода, одни и те же edge-cases, одни и те же тесты. Если YobaConf пушит события в workspace YobaLog'а, и имя workspace'а = имя ноды — правило валидации должно совпадать.
- Dot-нотация (`yobaproj.yobaapp.prod`) в URL — человекочитаемая, но требует запрета dot внутри сегмента. Иначе `a.b.c` неразличимо от `a` + `b.c`.
- `$`-префикс — устоявшаяся конвенция для служебных зарезервированных имён (см. MongoDB, Redis keys).

**Откатили:** более либеральный паттерн со слэшами (`a/b-c`) — конфликтует с URL-routing'ом; snake_case — не принят в Docker-экосистеме.

---

## 2026-04-21 — `bun` вместо `npm + node` для фронт-сборки (синхронно с YobaLog)

**Решение:** TypeScript + Tailwind + Monaco собираются через bun (встроенный bundler, TS из коробки, нативный Windows-бинарник). `package.json` рядом с `.csproj` в `src/YobaConf.Web`. Release — `bun install --frozen-lockfile && bun run build` из MSBuild target; Debug — параллельные watcher'ы.

**Причина:** bun быстрее, ставится одним бинарником без Node.js-окружения, TS-транспил уже встроен — не нужен отдельный `tsc` / `ts-loader` / `esbuild`. В yobalog уже прижилось, повторяем без расхождений.

**Откатили:** `npm + node + esbuild` (лишняя прослойка), `webpack` (оверкилл на объёме админки).

---

## 2026-04-21 — UI-компонент-библиотека: DaisyUI / Flowbite (выбор в первом frontend-спринте, синхронно с YobaLog)

**Решение:** готовая component-библиотека поверх Tailwind с тёмной темой из коробки (`dark`/`night`/`business`). Конкретная — выбирается в первом frontend-спринте синхронно с yobalog. Кастомизация запрещена — берём как есть.

**Причина:**
- Кастомная dark-тема = бесконечный сайд-проект. Готовая тема в DaisyUI/Flowbite закрывает 95% потребностей админки за 0 часов.
- Синхронизация с yobalog — минимизирует количество CSS-классов, которые нужно помнить.

**Откатили:** "пишем свою design-систему с нуля поверх Tailwind" — не оправдано масштабом админки.
