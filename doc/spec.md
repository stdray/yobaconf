# YobaConf: Централизованный сервис конфигураций

Иерархический сервис конфигураций как услуга (CaaS), использующий HOCON и авторизацию на основе путей.

## 1. Архитектурные принципы
- **Единый источник истины (SSoT).** Все настройки и секреты хранятся в центральном сервисе.
- **Иерархическое наследование.** Конфиги — узлы в дереве (по аналогии с файловой системой). Дочерние узлы наследуют и переопределяют настройки родительских.
- **Fallthrough (откат).** Если запрошенный специфичный узел (например, `app/dev/feature`) не существует, сервис автоматически отдаёт ближайший существующий родительский (`app/dev`).
- **Разделение форматов:**
    - **Для редактирования (сервер):** HOCON (поддерживает комментарии, инклуды, переменные, логику).
    - **Для доставки (клиент):** чистый JSON (уже собранный, со всеми подстановками).

## 2. Технологический стек
- **Платформа:** .NET 10.
- **База данных:** SQLite через `linq2db.SQLite.MS` (синхронно с yobalog — тот же стек, те же паттерны миграций, общий tooling опыт). Single-file `.db` на инстанс, WAL mode. Row-storage — выбор осознанный: модель rigid и flat, NoSQL-гибкости не использует (см. `decision-log.md` 2026-04-21 "SQLite + linq2db вместо LiteDB").
- **Движок конфигов:** `Hocon` 2.0.4 + `Hocon.Configuration` 2.0.4 (akkadotnet/HOCON, Apache-2.0). Phase A.1 HOCON-гейт закрыт (`decision-log.md` 2026-04-21). Transitive CVE на `System.Drawing.Common 4.7.0` запинена forward через CPM.
- **Безопасность:** AES-256-GCM (authenticated encryption) для секретов в БД — шифротекст + IV + версия мастер-ключа (на случай ротации). Мастер-ключ — env var `YOBACONF_MASTER_KEY`, никогда в `appsettings.json` / БД. API-ключи: scoped `RootPath`, токен = ShortGuid (22 chars base64url от 128-bit Guid, 122 бита энтропии), в БД — `sha256(token)` hex + 6-char prefix для UI (тот же формат, что в yobalog).
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
- `ScopePath` TEXT — путь, на котором видна переменная; наследуется потомками по тем же правилам, что RawContent (§4).
- `ContentHash` TEXT — sha256(Value) для optimistic lock.
- UNIQUE (`ScopePath`, `Key`).

`Secrets`:
- `Id` INTEGER PK.
- `Key` TEXT.
- `EncryptedValue` BLOB — AES-256-GCM шифротекст от plaintext под мастер-ключом.
- `Iv` BLOB — initialization vector (12 байт для GCM).
- `AuthTag` BLOB — GCM authentication tag (16 байт).
- `KeyVersion` TEXT — идентификатор версии мастер-ключа, которым зашифровано (для graceful rotation).
- `ScopePath` TEXT — те же правила видимости, что у Variables.
- `ContentHash` TEXT — sha256(EncryptedValue) для optimistic lock.
- UNIQUE (`ScopePath`, `Key`).

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
- `OldContent` TEXT | BLOB — предыдущее значение. Для `Kind = 'secret'` — encrypted blob + IV + AuthTag + KeyVersion (НИКОГДА plaintext).
- `ChangedAt` INTEGER — unix ms.
- `UserId` TEXT — автор изменения (для API-ключ-driven изменений = `TokenPrefix` ключа).

## 4. Бизнес-логика (конвейер обработки)
1. **Запрос.** Клиент шлёт `GET /v1/conf/{path}` + API-ключ.
2. **Авторизация (до любого lookup'а).** Валидация ключа + проверка, что запрошенный `path` — потомок `RootPath` ключа (посегментное сравнение, не `StartsWith`, см. §8). **403 раньше 404**: если ключ не даёт доступа — отвечаем `403` без намёка на существование ноды. Только когда доступ разрешён, идём в шаг 3.
3. **Поиск (ancestor chain для наследования).**
    - Fallthrough: ищем ближайшую существующую ноду от `path` вверх до корня, пропуская `IsDeleted=1`. Если ни одна не найдена — `404`.
    - Собираем весь ancestor chain (от root до best-match leaf), тоже пропуская soft-deleted ноды.
4. **Сборка HOCON.**
    - Подгрузка Variables + Secrets, чей `ScopePath` — ancestor текущего `path` (включая сам `path`). Ближайший scope перебивает дальний при коллизии Key.
    - Дешифровка Secrets по мастер-ключу (env) с учётом `KeyVersion`.
    - Рендер variables+secrets в HOCON-фрагмент (`key = "value"` на строку).
    - Конкатенация: `variables-hocon` + `parent-N RawContent` + ... + `parent-1 RawContent` + `leaf RawContent` (root → leaf).
    - Единый `HoconConfigurationFactory.ParseString(...)`. Substitutions резолвятся at parse-time (см. `decision-log.md` 2026-04-21 "Hocon 2.0.4 резолвит substitutions at parse-time").
    - **Ancestor-only inheritance:** MVP не поддерживает explicit `include`-директивы в `RawContent`; наследование — только через автоматический ancestor merge. Если `include` встречается в тексте — parse error. См. `decision-log.md` 2026-04-21 "Include-семантика".
5. **Сериализация.** HOCON-дерево → `System.Text.Json`. Объекты, строки, числа, bool, массивы — по прямому маппингу. HOCON-специфичная запись `a.b.c = 1` нормализуется в вложенные объекты.
6. **ETag и ответ.**
    - `ETag = first-16-hex-chars(sha256(rendered-json-bytes))`, strong ETag (без `W/` префикса).
    - Если `If-None-Match` header совпадает — `304 Not Modified`, пустое тело.
    - Иначе — `200 OK` + JSON + `ETag` header.

**Secret access model:** один scoped API-ключ даёт доступ и к переменным, и к секретам пути. Нет отдельной "read-secret" permission. Логика: раннер-клиент, которому достаётся конфиг, обязан знать все значения, чтобы работать. UI-админки маскирует значения секретов независимо (`******` + explicit reveal), но это UI-уровень, не API.

**Инвариант:** один и тот же вход (`path` + API key + состояние БД на момент запроса) обязан давать ровно один JSON-результат. Покрывается snapshot-тестами (см. `plan.md`).

## 5. Требования к UI (админка)
- **Проводник.** Дерево папок и узлов для навигации.
- **Редактор кода.** Monaco Editor с подсветкой синтаксиса HOCON (кастомный TextMate grammar — см. §11). Monaco оправдан, потому что HOCON-файлы бывают длинными, с includes и переменными — full editor experience окупается.
- **Предпросмотр.** Окно "результата", где виден итоговый JSON, который получит клиент.
- **Хранилище (Vault).** Управление секретами с маскировкой значений (`******`).

## 6. Масштабируемость и отказоустойчивость
- **Без кэша на старте.** Сборка "на лету" — SQLite indexed lookup по `Path` + in-memory merge HOCON. Soft target: p99 resolve+serialize < 50ms на дереве в 1k нод (меряем когда появится нагрузочный тест).
- **Поддержка ETag.** Финальный JSON → sha256 → 16-hex-chars strong ETag (§4.6). Клиент шлёт `If-None-Match` → `304` без тела.
- **Путь развития.** Экспорт ("push") готовых JSON в Redis, Consul или S3 для сверхвысоких нагрузок (Phase E).

## 7. Версионирование и аудит
- **Immutable history через `AuditLog`.** Любое изменение `Nodes.RawContent`, `Variables.Value`, `Secrets.EncryptedValue`, `ApiKeys.*` кладёт предыдущее значение в `AuditLog` (§3) с `ChangedAt` + `UserId`. Ретенция — **вечно**, без TTL (в отличие от логов в yobalog).
- **Секреты в аудите всегда encrypted.** Type-safe: `AuditLog.Kind='secret'` ветвь работает с blob + IV + AuthTag + KeyVersion, никогда с plaintext. Мастер-ключ для дешифровки старого snapshot'а — текущий env var (поэтому `KeyVersion` в `AuditLog` нужен — ротация ключа не ломает историю).
- **Soft delete.** Ноды помечаются `IsDeleted=1`, не удаляются физически. Fallthrough (§4) их пропускает — клиент получает результат, как если бы ноды не существовало. То же для Variables/Secrets (поле добавляется при первой реализации).
- **Restore удалённого = новая запись из истории.** В UI на странице ноды/переменной видна timeline `AuditLog`. "Восстановить" = создать новую запись с `RawContent`/`Value` из выбранного snapshot'а + `IsDeleted=0`. Механизм покрывает и undo accidental delete, и rollback к произвольной точке. Secret restore использует ту же ветку — encrypted blob перекладывается из `AuditLog` в `Secrets`.
- **Optimistic locking при редактировании.** Каждая таблица с пользовательским контентом (`Nodes`, `Variables`, `Secrets`) имеет `ContentHash` column. UI-редактор шлёт `expectedHash` при save → `UPDATE ... WHERE Id=@id AND ContentHash=@expected`; rows affected = 0 → conflict modal (three-way diff, inspired by stdray.Obsidian ConflictSolverService).
- **Diff в UI.** Текущая версия vs любой snapshot из `AuditLog` — текстовый diff на `RawContent` (HOCON-aware syntax highlighting через Monaco diff-editor).

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
- **Скрипты.** jQuery (для сложных UI-манипуляций) + Alpine.js (опционально, для простого локального стейта — например, открытия модалок).
- **Общие компоненты (shared library):**
    - Авторизация (Login/Logout).
    - Общий макет (Layout) на готовой component-библиотеке поверх Tailwind (DaisyUI / Flowbite) с тёмной темой из коробки (`dark`/`night`/`business`). Кастомизация запрещена. Конкретная библиотека выбирается в первом frontend-спринте синхронно с YobaLog.
    - Уведомления (toasts) через htmx-события.
- **Редактор конфигов.** Monaco Editor (интеграция через JS-interop).

## 10. Сборка фронта
- **Стек.** TypeScript + Tailwind + Monaco через npm, сборка через bun (встроенный bundler, TS из коробки, нативный Windows-бинарник).
- **Зависимости.** `package.json` рядом с `.csproj`, devDependencies: `monaco-editor`, `tailwindcss`, `typescript`. Сам bun — бинарник, не в `package.json`.
- **Dev.** Два терминала — `dotnet watch run` и `bun run dev` (параллельно `bun build ts/admin.ts --outdir=wwwroot/js --watch` + `tailwindcss -i ts/app.css -o wwwroot/css/app.css --watch`). CSS/JS — статика из `wwwroot`, браузер подхватывает без рестарта приложения.
- **Release.** MSBuild target `BeforeTargets="Build"` с `Condition="'$(Configuration)' == 'Release'"` — `bun install --frozen-lockfile && bun run build` (`build` = `typecheck` + минифицированные js/css). CI через `dotnet publish -c Release` собирает всё разом.
- **Monaco.** Подключается как npm-пакет (`import * as monaco from 'monaco-editor'`). Воркеры language-services билдятся как отдельные entry-points, URL-ы раздаются через `self.MonacoEnvironment.getWorkerUrl`. Для HOCON нужен кастомный TextMate grammar.

## 11. Self-observability
- Все server-side события (изменения нод, доступ по API-ключам, ошибки резолвинга, `403` по граничным путям) YobaConf пишет в YobaLog через CLEF endpoint. Rate-limiting в MVP не реализован (Phase E).
- API-ключ YobaConf → YobaLog хранится в `appsettings.json` самого YobaConf (**не** в YobaConf-ноде — иначе бутстрап-цикл, см. §2).
- Отдельный workspace в YobaLog под YobaConf (например, `$system/yobaconf` или выделенный `yobaconf-ops`) — не смешиваем с user workspaces.
- Рекурсия невозможна по конструкции: YobaLog не зависит от YobaConf, события YobaConf не могут триггерить обращение к YobaConf при записи.

## 12. Локализация
- **Стартовый язык:** английский. Русский — отложен, каркас предусматривает.
- **Механизм.** `IStringLocalizer` (ASP.NET Core Localization) + `.resx` ресурсы на culture. Ключи в коде — короткие английские идентификаторы.
- **Конвенция ключей.** Dot-notation (`page.nodes.breadcrumbs`, `errors.cycle_detected`).
- **Scope.** Все user-facing строки (UI labels, validation messages, API error responses). Хардкод в разметке/коде запрещён.
- **Frontend.** На SSR-рендере backend инжектит в `<head>` `window.__i18n = {...}` только с текущей локалью. TS читает как типизированный dict. Никаких extra-HTTP, никакого build-time кодогена.
- **Форматирование.** Числа, даты, pluralization — через culture-aware API. TZ пользователя хранится отдельно от language.
- **Переключение.** Per-user в профиле; не зависит от `Accept-Language` браузера.
