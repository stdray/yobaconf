# YobaConf: Централизованный сервис конфигураций

Иерархический сервис конфигураций как услуга (CaaS), использующий HOCON и авторизацию на основе путей.

## 1. Архитектурные принципы
- **Единый источник истины (SSoT).** Все настройки и секреты хранятся в центральном сервисе.
- **Каждый `.hocon` — отдельная нода.** В ментальной модели "filesystem" пути сегментированы по `/`; последний сегмент — имя файла, остальные — "директория". `.hocon`-расширение — визуальная конвенция (в DB хранится path без расширения). Примеры: `logger-base` в корне, `project-a/logger`, `project-a/test/service1`. Директория сама по себе нодой не является, пока в ней физически не создана нода с `RawContent`.
- **Иерархия — в путях, ACL и include-scope; не в автоматическом merge.** HOCON-контент между нодами **не сливается автоматически**. Каждая нода возвращает свой `RawContent` плюс **явно заинклуженные блоки**.
- **Explicit `include "absolute-path"` в RawContent.** Правило: `dir(target)` должен быть ancestor-or-equal `dir(including-node)`, где `dir(path)` = `path` без последнего сегмента. Это разрешает:
    - ancestors ноды (любые ноды в предковых директориях) ✓
    - siblings в той же директории (`project-a/test/service1` может включать `project-a/test/service2`) ✓
    - но **не** descendants текущей директории, не другие поддеревья (`project-a/dev/*` не видно из `project-a/test/*`).
    - **Циклы возможны** (мутуальные sibling-includes типа `service1 ↔ service2`) — детектируются runtime-обходом с `visited` set, violation → понятная ошибка.
    - Self-include (`include` себя же) — parse error.
    - Относительные пути (`../foo`) в MVP не поддерживаются. Только абсолютные — для явности при ревью.
- **Fallthrough (откат) — routing, не merge.** Если запрошенный `/a/b/c/x` не существует как нода — walk up, отдаём контент ближайшей существующей ancestor-ноды (`/a/b/c`, потом `/a/b`, ...). Если ни одна не найдена — 404. Работает только когда промежуточные сегменты сами являются нодами; если директории — чистые namespace без контента, fallthrough сразу даёт 404.
- **Переменные/секреты наследуются по scope.** Variables и Secrets видны всем descendants их `ScopePath` автоматически (без explicit include). Коллизия `Key` — побеждает ближайший scope. Scope-matching делается против **запрошенного path'а**, не против best-match ноды — чтобы env-специфичные vars работали даже при fallthrough'е к base-шаблону. Variable не появляется в результирующем JSON, пока на неё не сошлётся `${name}` из HOCON.
- **Разделение форматов:**
    - **Для редактирования (сервер):** HOCON (поддерживает комментарии, инклуды, переменные, логику).
    - **Для доставки (клиент):** чистый JSON (уже собранный, со всеми подстановками).

## 2. Технологический стек
- **Платформа:** .NET 10.
- **База данных:** SQLite через `linq2db.SQLite.MS` (синхронно с yobalog — тот же стек, те же паттерны миграций, общий tooling опыт). Single-file `.db` на инстанс, WAL mode. Row-storage — выбор осознанный: модель rigid и flat, NoSQL-гибкости не использует (см. `decision-log.md` 2026-04-21 "SQLite + linq2db вместо LiteDB").
- **Движок конфигов:** `Hocon` 2.0.4 + `Hocon.Configuration` 2.0.4 (akkadotnet/HOCON, Apache-2.0). Phase A.1 HOCON-гейт закрыт (`decision-log.md` 2026-04-21). Transitive CVE на `System.Drawing.Common 4.7.0` запинена forward через CPM.
- **Безопасность:** AES-256-GCM (authenticated encryption) для секретов в БД — шифротекст + IV + AuthTag + версия мастер-ключа (на случай ротации). **Каждый encrypt использует свежий IV**: `IV = RandomNumberGenerator.GetBytes(12)` перед каждой операцией. IV никогда не переиспользуется для пары `(key, plaintext)` — GCM с повторяющимся IV под одним ключом криптографически сломан (nonce-misuse → plaintext recovery). Тест на уникальность IV обязателен (записать один секрет дважды с одинаковым plaintext → IV разные). Мастер-ключ — env var `YOBACONF_MASTER_KEY`, никогда в `appsettings.json` / БД. API-ключи: scoped `RootPath`, токен = ShortGuid (22 chars base64url от 128-bit Guid, 122 бита энтропии), в БД — `sha256(token)` hex + 6-char prefix для UI (тот же формат, что в yobalog).
- **Сериализация:** `System.Text.Json`.
- **Конфигурация самого сервиса:** `appsettings.json`. YobaConf **не может** конфигурироваться через YobaConf — бутстрап-цикл. Мастер-ключ AES — только env var / CI secret.
- **Workspace-уровень отсутствует:** path-дерево — единственный namespace. Изоляция между проектами/командами — через API-ключи с `RootPath` на нужное поддерево. См. `decision-log.md` 2026-04-21 "Без workspaces в MVP".
- **Логирование:** YobaConf пишет свои события (изменения нод, аудит, ошибки резолвинга) в YobaLog через CLEF endpoint. Направление зависимости: **YobaConf → YobaLog**. Обратное запрещено (YobaLog `spec.md` §1: конфиг только из `appsettings.json`).

## 3. Модель данных (таблицы SQLite)

### Nodes (дерево конфигов)
- `Id` INTEGER PK.
- `Path` TEXT UNIQUE NOT NULL — канонический путь вида `projects/yoba/api/prod` (валидация per-segment по regex §8).
- `RawContent` TEXT NOT NULL — сырой HOCON.
- `ContentHash` TEXT — `sha256(RawContent)` hex, используется для optimistic locking в UI-редакторе (см. §7 и `decision-log.md` 2026-04-21 "Optimistic locking через ContentHash").
- `UpdatedAt` INTEGER — unix ms, участвует в ETag финального JSON и в истории.
- `IsDeleted` INTEGER (0/1) — soft delete (§7). Помеченные ноды пропускаются Fallthrough'ом.

### Variables (переменные) и Secrets (секреты) — две отдельные таблицы
**Разделение осознанное** — обоснование в `decision-log.md` 2026-04-21 "Variables и Secrets — отдельные таблицы". Коротко: разная schema (plaintext vs encrypted blob + IV + key-version), разный audit-path, type-safety на уровне DAO.

`Variables`:
- `Id` INTEGER PK.
- `Key` TEXT — имя переменной (`db_host`).
- `Value` TEXT — открытый текст.
- `ScopePath` TEXT — путь, на котором видна переменная; наследуется всеми descendants, ближайший scope перебивает дальний при коллизии Key.
- `ContentHash` TEXT — sha256(Value) для optimistic lock.
- `UpdatedAt` INTEGER — unix ms.
- `IsDeleted` INTEGER (0/1) — soft delete.
- UNIQUE (`ScopePath`, `Key`) WHERE `IsDeleted = 0`.

`Secrets`:
- `Id` INTEGER PK.
- `Key` TEXT.
- `EncryptedValue` BLOB — AES-256-GCM шифротекст от plaintext под мастер-ключом.
- `Iv` BLOB — initialization vector (12 байт для GCM).
- `AuthTag` BLOB — GCM authentication tag (16 байт).
- `KeyVersion` TEXT — идентификатор версии мастер-ключа, которым зашифровано (для graceful rotation).
- `ScopePath` TEXT — те же правила видимости, что у Variables.
- `ContentHash` TEXT — sha256(EncryptedValue) для optimistic lock.
- `UpdatedAt` INTEGER — unix ms.
- `IsDeleted` INTEGER (0/1) — soft delete.
- UNIQUE (`ScopePath`, `Key`) WHERE `IsDeleted = 0`.

### ApiKeys (ключи доступа)
- `Id` INTEGER PK.
- `TokenHash` TEXT — `sha256(token)` hex от выданного токена.
- `TokenPrefix` TEXT — первые 6 символов plaintext-токена, для идентификации в UI.
- `RootPath` TEXT — граница доступа; ключ на `yobaproj/yobaapp` видит `yobaproj/yobaapp/dev` и любого потомка, не видит ничего за пределами.
- `Description` TEXT — free-form человекочитаемое описание.
- `CreatedAt` INTEGER — unix ms.

Токен плейнтекст = ShortGuid (22 chars base64url от Guid.NewGuid, 122 бита). Показывается пользователю **один раз** при создании; потом в UI доступен только `TokenPrefix` для идентификации.

### AuditLog (immutable history, §7)
- `Id` INTEGER PK.
- `Kind` TEXT — `'node'` / `'variable'` / `'secret'` / `'apikey'`.
- `TargetId` INTEGER — ID строки в соответствующей таблице.
- `TargetPath` TEXT — путь ноды / `ScopePath` variable-or-secret'а. Денормализовано для UI-timeline (см. §7): админка показывает историю "по пути" унифицированно для nodes/variables/secrets.
- `OldContent` TEXT | BLOB — предыдущее значение. Для `Kind = 'secret'` — encrypted blob + IV + AuthTag + KeyVersion (никогда plaintext).
- `ChangedAt` INTEGER — unix ms.
- `UserId` TEXT — username админа из cookie-auth. В MVP single-admin (из `appsettings.json`); multi-admin история — Phase B+.

## 4. Бизнес-логика (конвейер обработки)
1. **Запрос.** Клиент шлёт `GET /v1/conf/{path}` + API-ключ.
2. **Авторизация (до любого lookup'а).** Валидация ключа + проверка, что запрошенный `path` — потомок `RootPath` ключа (посегментное сравнение, не `StartsWith`, см. §8). **403 раньше 404**: если ключ не даёт доступа — отвечаем `403` без намёка на существование ноды. Только когда доступ разрешён — шаг 3.
3. **Fallthrough (routing).** Ищем ближайшую существующую non-deleted ноду от `path` вверх до корня. Если ни одна не найдена — `404`. Результат — одна "best-match" нода; **никакого автомержа ancestors на этом этапе**.
4. **Сборка HOCON.**
    - Подгрузка Variables + Secrets, чей `ScopePath` — ancestor-or-equal **запрошенного пути** (не best-match). Ближайший scope перебивает дальний при коллизии Key.
    - Дешифровка Secrets по мастер-ключу (env) с учётом `KeyVersion`.
    - Рендер variables+secrets в HOCON-фрагмент (`key = "value"` на строку) — становится "defaults" слоем.
    - **Резолвинг `include`-директив — отдельная preprocess-стадия** (не через HOCON-native callback): DFS с `visited: HashSet<NodePath>`. Для каждого `include "absolute-path"`:
        - валидация scope: `dir(target)` должен быть ancestor-or-equal `dir(including-node)`; self-include запрещён. Нарушение → `IncludeScopeViolation` с понятным сообщением.
        - cycle check: target уже в `visited` → `CyclicIncludeException` с цепочкой.
        - добавить target в `visited`, загрузить его `RawContent`, рекурсивно раскрыть его include-директивы.
        - подставить результат в текст включающей ноды.
        - На выходе: **плоский HOCON-текст без `include`-директив**.
    - Причина preprocess'а вместо HOCON `ConfigResolver` callback'а: native callback не получает контекст "кто включает", из-за чего scope-валидация и cycle-detection не выражаются на его API чисто. Preprocess — полный контроль, тот же набор возможностей + явная ошибка вместо непонятного вылета парсера.
    - Склейка: `variables-hocon` + `flattened-best-match-hocon` (variables перекрываются контентом ноды — стандартная Lightbend-конвенция "defaults first, then overrides").
    - Единый `HoconConfigurationFactory.ParseString(...)`. Substitutions (`${var}`) резолвятся at parse-time (см. `decision-log.md` 2026-04-21 "Hocon 2.0.4 резолвит substitutions at parse-time"). HOCON-substitution-циклы (`a = ${b}, b = ${a}` в одном тексте) ловятся парсером — отдельный test в plan.md.
5. **Сериализация.** HOCON-дерево → `System.Text.Json`. Объекты, строки, числа, bool, массивы — прямой маппинг. HOCON-специфичная запись `a.b.c = 1` нормализуется во вложенные объекты.
6. **ETag и ответ.**
    - `ETag = first-16-hex-chars(sha256(rendered-json-bytes))`, strong ETag (без `W/` префикса).
    - Если `If-None-Match` header совпадает — `304 Not Modified`, пустое тело.
    - Иначе — `200 OK` + JSON + `ETag` header.

**Secret access model:** один scoped API-ключ даёт доступ и к переменным, и к секретам пути. Нет отдельной "read-secret" permission. Логика: раннер-клиент, которому достаётся конфиг, обязан знать все значения, чтобы работать. UI-админки маскирует значения секретов независимо (`******` + explicit reveal), но это UI-уровень, не API.

**Инвариант:** один и тот же вход (`path` + API key + состояние БД на момент запроса) обязан давать ровно один JSON-результат. Покрывается snapshot-тестами (см. `plan.md`).

## 5. Требования к UI (админка)
- **Проводник.** Дерево папок и узлов для навигации.
- **Подсветка синтаксиса (read-only view, Phase A).** [Prism.js](https://prismjs.com/) + кастомный HOCON-компонент (порт из [sabieber/vscode-hocon](https://github.com/sabieber/vscode-hocon) TextMate grammar, ~80 строк регексов). Используется для read-only отображения `RawContent` в дереве и для результирующего JSON в preview-панели. ~20-25 KB bundle.
- **Редактор кода (edit mode, Phase B).** [CodeMirror 6](https://codemirror.net/) с HOCON `StreamLanguage`-токенайзером (порт той же TextMate-грамматики, ~150 строк). Причина CodeMirror вместо Monaco — decision-log 2026-04-21 "CodeMirror 6 + Prism вместо Monaco Editor".
- **Diff view.** `@codemirror/merge` (~30 KB addon) — compare текущей версии ноды с любым snapshot'ом из AuditLog (§7).
- **Preview-панель резолвинга (Phase A, прямо над деревом или справа).** Для выбранной ноды показывает итоговый JSON после полного §4 pipeline (Fallthrough + variables + includes + substitution). Prism JSON-подсветка (grammar из коробки). ETag + информация о том, какие variables/secrets/includes участвовали — в под-панели для трассировки "почему так собралось".
- **Import from paste (Phase A, "New node from…" форма).** Textarea + format-dropdown (JSON / YAML / `.env`) + кнопка "Convert" → показывает preview сконвертированного HOCON в правой панели → "Save as node" создаёт новую ноду с этим `RawContent`. Use case: миграция существующих конфигов (k8s `values.yaml`, production `.env`, REST API JSON-responses) без ручного переписывания в HOCON. Конвертеры живут в Core как чистые функции — см. `decision-log.md` 2026-04-21 "Import converters: JSON / YAML / .env". **Cap на размер входа:** 1 MiB. Превышение → 413 Payload Too Large с человеческим сообщением. Без cap'а большой YAML (vulgar 50 MiB) может съесть память YamlDotNet'а до OOM.
- **Хранилище (Vault).** Управление секретами с маскировкой значений (`******`).

## 6. Масштабируемость и отказоустойчивость
- **Без кэша на старте.** Сборка "на лету" — SQLite indexed lookup по `Path` + include-резолвинг + in-memory HOCON parse. Целевой профиль — pet-projects, нагрузка около 0. Performance target не фиксируется заранее: когда появится нагрузочный тест — baseline фиксируется в `perf-baseline.md` (зеркально yobalog), дальше следим за регрессией.
- **Поддержка ETag.** Финальный JSON → sha256 → 16-hex-chars strong ETag (§4.6). Клиент шлёт `If-None-Match` → `304` без тела.
- **Путь развития.** Экспорт ("push") готовых JSON в Redis, Consul или S3 для сверхвысоких нагрузок (Phase E).

## 7. Версионирование и аудит
- **Immutable history через `AuditLog`.** Любое изменение `Nodes.RawContent`, `Variables.Value`, `Secrets.EncryptedValue`, `ApiKeys.*` кладёт предыдущее значение в `AuditLog` (§3) с `ChangedAt` + `UserId`. Ретенция — **вечно**, без TTL (в отличие от логов в yobalog).
- **Секреты в аудите всегда encrypted.** Type-safe: `AuditLog.Kind='secret'` ветвь работает с blob + IV + AuthTag + KeyVersion, никогда с plaintext. Мастер-ключ для дешифровки старого snapshot'а — текущий env var (поэтому `KeyVersion` в `AuditLog` нужен — ротация ключа не ломает историю).
- **Soft delete.** Ноды помечаются `IsDeleted=1`, не удаляются физически. Fallthrough (§4) их пропускает — клиент получает результат, как если бы ноды не существовало. То же для Variables/Secrets (поле добавляется при первой реализации).
- **Unified timeline в UI.** На странице path `/a/b/c` показывается **единая timeline** событий `AuditLog` для этого пути: изменения ноды (Kind=node), Variables с `ScopePath` = этот path, Secrets с тем же ScopePath, изменения API-ключей с RootPath = этот path. Навигация prev/next-state работает через объединённую history, не раздельно по сущностям — рассуждения пользователя типа "что происходило в /yobapub/prod вчера" не должны требовать переключения между тремя вкладками.
- **Restore удалённого = новая запись из истории.** Timeline показывает snapshot'ы с действием "Restore this version" — одна кнопка создаёт новую запись в target-таблице с контентом из snapshot'а + `IsDeleted=0`. Покрывает undo accidental delete и rollback к произвольной точке. Secret restore использует ту же ветку (encrypted blob перекладывается из `AuditLog` в `Secrets`).
- **Optimistic locking при редактировании.** `Nodes`, `Variables`, `Secrets` — все содержат `ContentHash` column. UI-редактор шлёт `expectedHash` при save → `UPDATE ... WHERE Id=@id AND ContentHash=@expected`; rows affected = 0 → conflict modal (three-way diff, inspired by stdray.Obsidian ConflictSolverService).
- **Diff в UI.** Текущая версия vs любой snapshot из `AuditLog` — текстовый diff на `RawContent`/`Value` через `@codemirror/merge` с HOCON-подсветкой (та же StreamLanguage, что в основном редакторе).

## 8. Дизайн API и маршрутизация
- **URL-структура:** `GET /v1/conf/{path}`.
- **Разделитель.** Внешние пути используют точку (`.`) для иерархии.
- **Валидация.** Имена нод — slug в стиле Docker image names, regex `^[a-z0-9][a-z0-9-]{1,39}$` (синхронно с workspace ID в YobaLog). Точка запрещена внутри имени — зарезервирована для обозначения вложенности. Префикс `$` зарезервирован для системных нод (`$system`, `$bootstrap`).
- **Пример.**
    - Клиент: `GET /v1/conf/yobaproj.yobaapp.prod`
    - Сервер преобразует в путь БД: `yobaproj/yobaapp/prod`
- **Безопасность.** API-ключ проверяется на соответствие префиксу пути. Ключ, выданный на `yobaproj.yobaapp`, разрешит доступ к `yobaproj.yobaapp.dev`, но не к `yobaproj.otherapp`.

## 9. Фронтенд и UI-технологии
- **Движок.** ASP.NET Core Razor Pages (SSR).
- **Интерактивность.** htmx (динамическая подгрузка контента без перезагрузки страницы).
- **Скрипты.** Собственный TS в `ts/admin.ts` (минимум); Alpine.js **опционально** для простого локального стейта (модалки, toggle'ы). **jQuery не используется** — для YobaConf scope (дерево + CodeMirror + пара модалок) не даёт того, что не даст Alpine + htmx. См. `decision-log.md` 2026-04-21 "Drop jQuery from UI stack".
- **Общие компоненты (shared library):**
    - Авторизация (Login/Logout).
    - Общий макет (Layout) на готовой component-библиотеке поверх Tailwind (DaisyUI / Flowbite) с тёмной темой из коробки (`dark`/`night`/`business`). Кастомизация запрещена. Конкретная библиотека выбирается в первом frontend-спринте синхронно с YobaLog.
    - **Screen inventory + UX-паттерны для Phase B реализации** — `doc/ui-reference.md` (экраны, layout'ы, UX-микро-решения из wireframe-export'а; визуальный стиль wireframe'ов отброшен — нарушает "кастомизация запрещена").
    - Уведомления (toasts) через htmx-события.
- **Редактор конфигов.** Monaco Editor (интеграция через JS-interop).

## 10. Сборка фронта
- **Стек.** TypeScript + Tailwind + CodeMirror 6 (Phase B) + Prism (Phase A read-only) через npm, сборка через bun (встроенный bundler, TS из коробки, нативный Windows-бинарник).
- **Зависимости.** `package.json` рядом с `.csproj`. Фаза A.0 (текущая): `concurrently`, `daisyui`, `tailwindcss`, `typescript`, `@biomejs/biome`. Добавляются по мере появления фич: `prismjs` (Phase A read-only UI), `codemirror` + `@codemirror/state` + `@codemirror/view` + `@codemirror/language` + `@codemirror/merge` + `@codemirror/legacy-modes` (Phase B edit). Сам bun — бинарник, не в `package.json`.
- **Dev.** `./build.sh --target=Dev` (или `pwsh ./build.ps1 -Target Dev`) — одна Cake-task, запускает `bun run dev` + `dotnet watch` в одной консоли. Ctrl+C убивает оба process tree. Раньше был `run_dev.ps1` c двумя окнами — удалён в коммите a5ad45c.
- **Release.** MSBuild target `BeforeTargets="Build"` с `Condition="'$(Configuration)' == 'Release'"` — `bun install --frozen-lockfile && bun run build` (`build` = `typecheck` + минифицированные js/css). CI через Cake `DockerPush` таргет собирает всё разом.
- **CodeMirror 6 + HOCON.** В Phase B добавится `ts/hocon-mode.ts` — `StreamLanguage`-токенайзер HOCON (ручной порт из [sabieber/vscode-hocon](https://github.com/sabieber/vscode-hocon) TextMate grammar, ~150 строк). StreamLanguage хватает для highlighting + basic indent; полноценный Lezer grammar с AST — откладывается до use-case'а semantic-фич (go-to-include-target, autocomplete по `${var}`).
- **Prism + HOCON.** В Phase A добавится `ts/prism-hocon.ts` — кастомный Prism language component из той же TextMate-базы (~80 строк regex). Для read-only отображения в дереве и JSON-preview (JSON grammar у Prism из коробки).

## 11. Развёртывание и HTTPS
- **Docker-образ + независимый deploy.** YobaConf деплоится собственным CI (build.cake → DockerPush → `docker run` через SSH). Никакого docker-compose в общем lifecycle'е с другими сервисами — projects независимы и выкатываются по отдельным тегам `deploy`. Паттерн зеркалит yobapub: image идёт в ghcr.io, SSH-деплой на хосте делает `docker pull` + `docker run -d` с уникальным именем контейнера.
- **Host-port convention.** Контейнер YobaConf биндится на `127.0.0.1:8081` (loopback only — no direct public exposure). Ports per project on the shared host:
    - `8080` — yobapub (existing, pre-Caddy era)
    - `8081` — yobaconf
    - `8082` — yobalog (reserved для их deploy)
    - следующие свободные — для future services
  Allocation-таблица продублирована в `infra/Caddyfile.fragment` (rooted здесь же в репо) для быстрого grep'а при добавлении нового сервиса.
- **HTTPS через Caddy (host-level reverse proxy).** Central Caddy на хосте терминирует TLS на `:443`, proxy'ит к loopback-портам проектов. Выбран вместо nginx+certbot: Let's Encrypt renewal встроен (cron-less), конфиг — одна строка на сервис, reload без downtime. Подробности выбора — `decision-log.md` 2026-04-21 "Caddy on host as HTTPS terminator".
- **Caddyfile-fragment живёт в `infra/Caddyfile.fragment`** в репо как reference (не consumed Caddy'ом напрямую). Центральный `/etc/caddy/Caddyfile` собирается из этих fragment'ов (вручную или через отдельный infra-repo — TBD). Renewal автоматический; cert-файлы хранятся в `/var/lib/caddy/`.
- **Forwarded-headers wiring в ASP.NET.** Caddy устанавливает `X-Forwarded-Proto=https` / `X-Forwarded-For`. YobaConf конфигурируется `app.UseForwardedHeaders(new ForwardedHeadersOptions { KnownProxies = { IPAddress.Loopback } })` (Phase A deploy bullet). Без этого `HttpContext.Request.IsHttps == false` за proxy, `UseHttpsRedirection` уходит в loop, cookie `Secure`-flag рассчитывается неправильно.
- **Первичный bootstrap хоста** (делается один раз вручную): `apt install caddy` + начальный Caddyfile + `systemctl enable caddy` + firewall-правила на 80/443. После этого добавление нового сервиса = (1) DNS A-запись на server IP, (2) docker run на выделенный loopback-port, (3) строка в центральном Caddyfile, (4) `caddy reload`. Шаги (2)-(4) автоматизируемы через `./build.sh --target=DockerPush` + deploy-job.

## 12. Self-observability
- Все server-side события (изменения нод, доступ по API-ключам, ошибки резолвинга, `403` по граничным путям) YobaConf пишет в YobaLog через CLEF endpoint. Rate-limiting в MVP не реализован (Phase E).
- API-ключ YobaConf → YobaLog хранится в конфиг-пространстве самого YobaConf (**не** в YobaConf-ноде — иначе бутстрап-цикл, см. §2). В dev — через `dotnet user-secrets`; в prod — env vars `YobaLog__ServerUrl` + `YobaLog__ApiKey`, инъекция из GitHub secrets в `docker run` (см. `.github/workflows/ci.yml`, `doc/deploy.md` Step 6).
- Реализация: `Seq.Extensions.Logging` 9.0.0, провайдер в `ILoggingBuilder` — шлёт `ILogger<T>`-события как CLEF на `{ServerUrl}/api/events/raw` (Seq-compat namespace yobalog'а `/compat/seq`). См. `decision-log.md` 2026-04-21 "Self-observability via Seq.Extensions.Logging".
- **Workspace — общий `apps-prod`**, не изолированный `yobaconf-ops`. Причина — yobalog MVP не умеет cross-workspace KQL; для cross-service trace-correlation нужен shared WS. Различение по CLEF-полю `App`. См. `decision-log.md` 2026-04-21 "Logging policy: shared workspace".
- **Field taxonomy, enrichment, retention-политика** — `doc/logging-policy.md` (самодостаточный файл для копирования в консьюмер-проекты).
- Отдельный workspace в YobaLog под YobaConf (например, `$system/yobaconf` или выделенный `yobaconf-ops`) — не смешиваем с user workspaces.
- Рекурсия невозможна по конструкции: YobaLog не зависит от YobaConf, события YobaConf не могут триггерить обращение к YobaConf при записи.

## 13. Локализация
- **Стартовый язык:** английский. Русский — отложен, каркас предусматривает.
- **Механизм.** `IStringLocalizer` (ASP.NET Core Localization) + `.resx` ресурсы на culture. Ключи в коде — короткие английские идентификаторы.
- **Конвенция ключей.** Dot-notation (`page.nodes.breadcrumbs`, `errors.cycle_detected`).
- **Scope.** Все user-facing строки (UI labels, validation messages, API error responses). Хардкод в разметке/коде запрещён.
- **Frontend.** На SSR-рендере backend инжектит в `<head>` `window.__i18n = {...}` только с текущей локалью. TS читает как типизированный dict. Никаких extra-HTTP, никакого build-time кодогена.
- **Форматирование.** Числа, даты, pluralization — через culture-aware API. TZ пользователя хранится отдельно от language.
- **Переключение.** Per-user в профиле; не зависит от `Accept-Language` браузера.
