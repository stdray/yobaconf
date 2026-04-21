# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

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

## 2026-04-21 — Include поддерживаем, не откладываем: explicit merge, auto ancestor-merge убираем (переворот ранее принятого)

**Решение:** Поддерживаем explicit `include "ancestor-path"` в `RawContent` с самого MVP Phase A. Target include-директивы должен быть **proper ancestor** включающей ноды по path — sibling/descendant/cross-tree include = parse error. Автоматическое наследование ancestor-chain из spec §1 **убирается**: нода возвращает ровно тот контент, который в ней записан, плюс явно заинклуженные блоки. Fallthrough (spec §1, §4.3) остаётся как routing-механика ("несуществующий path → ближайший ancestor"), но возвращает только один best-match узел без автомержа.

**Причина (возражение пользователя к предыдущему решению):**
- Auto-merge неявен: автор test.hocon получает logger-base в результирующем JSON, даже если не хотел — у него нет способа "отказаться от наследования". Explicit include = pull model, автор сам решает.
- Предсказуемость: контент ноды = ровно то, что видно в редакторе (плюс явные include). Никакой скрытой магии из родителей — проще отлаживать, проще ревьюить.
- Явность зависимости в коде: `include "yobapub"` в test.hocon самодокументирует, откуда берутся defaults. При auto-merge эта связь нигде не записана — нужно держать в голове структуру дерева.
- Ancestor-restriction защищает от циклов по конструкции (ссылки только вверх) и от cross-tree leak (нельзя увести чужое поддерево).

**Откатили:** "Include-семантика: только auto ancestor-merge, explicit отложен" (то же число, раньше сегодня). Переворот после возражения пользователя: "я предполагал, что пользователь руками всегда будет включать нужные блоки, чтобы никакого неявного поведения и случайного засорения конфига". Аргумент принят.

**Открытый вопрос (вынесен в чат):** как мапятся файлы из filesystem-drawing пользователя на path'ы YobaConf — logger-base.hocon это root-node content (path="") или отдельный sibling-node (path="logger-base")? Первое делает правило "only ancestors" чистым; второе требует дополнительного исключения для root-level shared nodes. Ждём подтверждение, логика резолвера зависит от ответа.

---

## 2026-04-21 — ~~Include-семантика: только автоматическое наследование ancestors, explicit `include` отложен~~ (переработано, см. выше)

**Решение:** ~~В MVP Phase A единственный механизм merge — автоматический обход ancestor chain...~~

**SUPERSEDED 2026-04-21** записью "Include поддерживаем, не откладываем" (выше). Оставлена как исторический контекст — решение существовало около часа между двумя обсуждениями, не попало в код.

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
