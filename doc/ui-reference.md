# UI reference — Phase B design source

## TL;DR

Wireframe-export, заказанный через `claude.ai/design` (бриф — в `decision-log.md` 2026-04-22). Даёт **структуру экранов, layout-паттерны и UX-микро-решения** для Phase B реализации. Исходники не коммитим — визуальный стиль (custom palette, Caveat cursive, SVG-wobble) **противоречит** spec §UI инварианту "готовая component-библиотека поверх Tailwind, кастомизация запрещена".

Этот файл — кристаллизация того, **что берём, что отбрасываем, почему**.

Оригинальные материалы у владельца локально в `tmp/yobaconf/` (gitignored). При реализации Phase B UI — держать split-view с `screens-*.jsx` как референс структуры; переводить в Tailwind-классы поверх DaisyUI stock theme'ы, не инлайн-стили.

## Проверка на совместимость со spec §UI

Правило (`spec.md` §UI, `decision-log.md` 2026-04-21 "UI-компонент-библиотека: DaisyUI / Flowbite"): **стандартные DaisyUI-компоненты + одна из стоковых тёмных тем (`dark` / `night` / `business`), никакой кастомной дизайн-системы, никаких собственных палитр.**

| Элемент wireframe'а | Вердикт | Причина |
|---|---|---|
| Компоновка страниц, state'ы, UX-flow | ✅ Берём | Это структурные решения, не визуальные. Tailwind + DaisyUI реализуют один-в-один. |
| Component-tree (Nav, KVTable, HoconEditor, Timeline, Drawer) | ✅ Берём | Готовые DaisyUI primitives (`table`, `card`, `drawer`, `timeline`) покрывают всё; Razor-partials декомпозируются по той же границе. |
| UX-микро-паттерны (inline-add, reveal-with-tinting, origin-entry dim, inherited badge) | ✅ Берём | Тонкие UX-решения, реализуются комбинацией стандартных utility-классов. |
| Палитра (paper cream `#f5f1e8` + coral `oklch(0.65 0.12 30)`) | ❌ Отбрасываем | Custom palette, инвариант "кастомизация запрещена". Берём `business` / `night` / `dark` стоковые. |
| Caveat cursive display-font | ❌ Отбрасываем | Low-fi wireframe-декор. Hi-fi — system-sans (DaisyUI default) или тот, что в yobalog. |
| SVG "rough" filter (feTurbulence + feDisplacementMap) | ❌ Отбрасываем | Чисто sketchy-decor для wireframe-эстетики. Clean strokes в prod. |
| JetBrains Mono для monospace | ✅ Берём | Стандарт в стеке (yobalog тоже использует). Не кастомизация, а font-family choice, который DaisyUI не диктует. |
| Diff pastel colors (`#ffd6d6` / `#d6ffd6`) | 🟡 Адаптируем | Идея правильная (мягкая подсветка), но конкретные hex заменим на DaisyUI `error-content` / `success-content` с alpha-opacity. |
| Pixel-exact spacing (44px nav, 400px drawer) | 🟡 Rough-guide | Как referenceorientation, но снапиться на Tailwind spacing-scale (`h-12` = 48px для nav, `w-96` = 384px для drawer). |

## DaisyUI theme — `dark` (зафиксирована, не трогаем)

Активная — **`dark`** (см. `data-theme="dark"` в `_Layout.cshtml`, `themes: ["dark", "night", "business"]` в `tailwind.config.js`). При реализации Phase B UI все цвета через DaisyUI semantic-классы (`bg-base-100`, `text-base-content`, `bg-primary`, `text-accent` и т.д.) — тема переключается одной переменной, никаких хардкодов hex.

Остальные две (`night`, `business`) доступны через theme-toggle в nav-bar — резерв, не дефолт.

## Screen inventory

Из wireframe'а всего 14 состояний на 10 экранах. Priority для Phase B UI реализации:

| Экран / state | Приоритет | Notes |
|---|---|---|
| `/Index` — indented tree (Variant A) | A | Главный landing. Не Miller columns и не cards — они фолбэки для deep-tree / small-scale, yobaconf'у не подходит |
| `/Index` — empty state | A | First-run experience, 2 CTAs (empty + paste-import) |
| `/Node` — default view | A | Главный рабочий экран: breadcrumbs + ETag + HOCON editor + Variables + Secrets + Resolved JSON |
| `/Node` — edit mode + conflict bar | A | Inline conflict warning, не blocking modal |
| `/Node` + History drawer open | A | 400px slide-in, 5 entries + "View full timeline →" |
| `/Node` — mobile (375×800) | B | Accordion для Variables/Secrets, HOCON full-width |
| `/Node` — light theme | C | Тема-toggle проверка |
| `/History?path=X` full page | A | Filters sidebar + day-grouped timeline + bulk rollback |
| Rollback confirm modal | A | Divergent-state warning + preview финального diff + key-rotation warning |
| `/Import` classify step | A | Mask-by-default + reveal + Keep/Variable/Secret radios + running counter |
| Three-way conflict modal | B | Full-screen, per-hunk cherry-pick + editable merged |
| `/Login` | C | Simple card, attempt counter |
| `/Index` — Miller columns (Variant B) | — | Rejected. Deep-tree-only, yobaconf типично 2-4 уровня |
| `/Index` — grouped cards (Variant C) | — | Rejected. Не масштабируется >30 нод |

## Adopted layout patterns

### Nav bar (48px)
Слева: brand `YobaConf` + muted version-label. По центру: таб-ссылки (`Configs` / `Import` / `History` + active-underline). Справа: username + `Sign out` + theme-toggle (sun/moon). Уже реализовано в `_Layout.cshtml`, менять не надо кроме добавления `History` таба для Phase B.

### `/Index` tree row
Single-line, `padding-left` зависит от `depth` (в JSX — `8 + depth*20`, в Tailwind — `pl-2 + pl-${depth * 5}` или dynamic style). Слева направо: chevron (▶/▼ для branch, spacer для leaf) · glyph (● actual / ○ virtual / ◌ empty) · mono path-segment · flex-spacer · state-badges (`3v` `1s` pills) · timestamp. Row dotted bottom-border между соседями.

Monochrome glyphs: actual в accent, virtual/empty в muted. Badges пилл-shaped, monospace, 10px. Timestamp — `edited 2d ago`, muted, right-aligned, min-width 90px.

### `/Node` — 3-section stack

```
┌────────────────────────────────────────────────┐
│ HEADER: breadcrumbs + ETag badge + timestamp   │
│         + [Edit][History][Export][Delete]      │
├────────────────────────────────────────────────┤
│ HOCON EDITOR (CodeMirror 6, readonly by def)   │
│ + conflict bar inline on top if detected       │
├──────────────────────┬─────────────────────────┤
│ VARIABLES table      │ SECRETS table           │
│ [+ Add variable]     │ [+ Add secret]          │
├──────────────────────┴─────────────────────────┤
│ ▼ RESOLVED JSON (collapsible)                  │
└────────────────────────────────────────────────┘
```

Max-width контейнера `~1280px`. Variables/Secrets на wide — бок-о-бок, на narrow — stack с accordion. Resolved JSON default-collapsed.

### `/Node` + History drawer
`w-96` (384px) slide-in из правого края, push page-content (не overlay). Sticky header (title + close), scrollable body (5 entries с vertical dashed-rail + colored circles), sticky footer (`View full timeline →`).

### `/History` full page
2-column layout на wide: filters sidebar (`w-72`, 288px) слева + timeline `flex-1` справа. На narrow — filters в drawer. Timeline — day-grouped с separator'ами, каждый event на dashed vertical rail с cirlce-marker.

## Adopted UX micro-patterns (важно)

Это ценные решения сверх brief'а, явно берём:

1. **`⇣ inherited` badge** рядом с Variable key, если value приходит с ancestor-node. Пользователь видит "этот определён не здесь, здесь только наследуется". Tailwind: `<span class="text-xs text-base-content/50 ml-1">⇣ inherited</span>`.

2. **Revealed-row tinting**: при click'е на 👁 на Secret — не только plaintext показывается, но ВСЯ row получает accent-bg. Визуальный маркер "этот row сейчас out of safe state, скрой обратно". Tailwind: `<tr class="bg-accent/10">` при reveal.

3. **Resolved JSON — `"(secret)"` плейсхолдер**. Secrets никогда не decrypt'ятся в preview-панели. Literally
   ```json
   "password": "(secret)"
   ```
   с accent-highlighted span'ом. Безопаснее чем отдельный /Reveal endpoint, пользователь не может случайно скопировать plaintext из JSON view.

4. **Origin-entry в timeline** — создание ноды специально greyed (circle в muted, text "← origin · cannot roll back"). Явно communicates semantics rollback'а.

5. **Inline-add row**: раскрывается ПОД последней row'й в KV-таблице, dashed-accent top-border (`border-t border-dashed border-accent`), background-tint (`bg-accent/5`). Визуально прикрепляется к таблице, не modal.

6. **Conflict bar — inline на editor'е**, не modal. Alert-warning pattern в DaisyUI: `<div class="alert alert-warning">⚠ another session saved this 10s ago... [Show 3-way diff]</div>`.

7. **Diff +/- tinting** как pastel-tones (`success-content/20` bg + darker text), не full-saturated. На светлом фоне paper-tone читается мягко, не рвёт глаза. Для dark theme обернуть в `dark:` variant с более тёмными оттенками.

8. **Pulse-dot на History-кнопке** если нода правилась <1 минуты назад. `<span class="absolute -top-0.5 -right-0.5 w-2 h-2 bg-accent rounded-full animate-pulse">`. Attention-hint в header-bar.

9. **Attempt counter на /Login** ("attempt 2 of 5") — rate-limit transparency. Когда добавим rate-limit (Phase B+) — UI готов.

10. **Master-key rotation warning на rollback** — Phase C+1 concern, заранее показано в mockup'е. Запомнить при реализации rollback'а для secrets.

## Rejected (явно)

1. **Custom palette** (paper cream + coral) — violates spec §UI. Стоковая DaisyUI theme.
2. **Caveat cursive** — wireframe-декор. System-sans в hi-fi.
3. **SVG wobble filter** — wireframe-декор. Clean strokes.
4. **Miller columns & grouped cards** variants для /Index — yobaconf's tree scale (2-4 depth, до сотен нод) не попадает в их use-case (deep trees / <30 nodes).
5. **Inline styles** — все стили через Tailwind utility-классы + DaisyUI component-классы.

## Cross-refs

- `doc/spec.md` §UI — invariant "кастомизация запрещена" (нарушение wireframe-stylingом не берём)
- `doc/decision-log.md` 2026-04-21 "UI-компонент-библиотека: DaisyUI / Flowbite" — корневое решение
- `doc/decision-log.md` 2026-04-22 "Wireframe extraction: structure yes, visual style no" — эта запись
- `doc/plan.md` Phase B bullets — UI реализация с этого файла как source of truth
