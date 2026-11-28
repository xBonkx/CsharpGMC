# План приёмки perfacc-A — 2026-11-29 (не реестр броней)

**Перенесено в канонический [ACCEPTANCE_TODO](ACCEPTANCE_TODO.md).** Все 30 броней исходного агента сохранены с тем же владельцем; таблица ниже — исходный план и исторический снимок, не актуальная занятость. Не брать/освобождать задачи здесь; перед продолжением zcode-sess-perfacc должен перечитать свежий main и подтвердить владение своими строками в ACCEPTANCE_TODO. Результаты кампаний хранить здесь или в профильном отчёте, а статус/бронь/evidence обновлять в канонической очереди. Исходные планы и критерии сохранены.

## Исходная бронь perfacc-A (30 ID, модуль PERFORMANCE TODO)

- Агент/сессия: `zcode-sess-perfacc`
- Зарегистрировано: 2026-11-29T00:55Z
- База: main `8caa525e` (fetch/сверка выполнена; работа в отдельном worktree, чужие незакоммиченные правки в основном workspace не затрагиваются)
- Scope: только выполнение существующих приёмочных харнессов (RemoteSqlBench + diagnostics, Pilot.E2E, App.Checks, StartupTrace, Invoke-LocalAcceptance) на разрешённой демо-БД `GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` (lpc:.); правки кода — только при обнаруженном дефекте, с регрессионным тестом. Пересечений с бронями `zcode-sess-perf714` (PERF-007/014, PARTIAL) и `zcode-sess-design` (CTX/AGR/ARCH) нет.

| ID | Прежний статус | Критерий приёмки (кратко) | Исходный план на момент переноса |
|---|---|---|---|
| PERF-001 | CODE READY | CSV/JSON фаз запуска, сумма объясняет задержку | ☑ план C1 |
| PERF-005 | CODE READY | Startup: оболочка до I/O, состояния loading/ready/error/retry | ☑ план C1 |
| PERF-009 | CODE READY | Кэш-открытие с фоновой проверкой ревизии, запрет записи до сверки | ☑ план C6 |
| PERF-010 | CODE READY | Инвалидация по meta.rev: чужая правка, отказ stale-write, reuse после подтверждения | ☑ план C4 |
| PERF-011 | CODE READY | Замер hit/miss, A→B→A, память и время проверки кэша | ☑ план C6 |
| PERF-012 | CODE READY | Реальная отмена чтений, A→B→C: виден только C | ☑ план C6/C4 |
| PERF-013 | CODE READY | Раздельные замеры SQL read/revision/Undo/mapping/binding | ☑ план C6 |
| PERF-015 | CODE READY | Undo: первый edit, checkpoint после записи, SELECT * объём | ☑ план C6 |
| PERF-016 | CODE READY | Очередь записи: пакет, порядок, expected revision, 2500+ паста | ☑ план C6/C4 |
| PERF-017 | CODE READY | Таб-переключение по ревизии, без лишних перечитываний | ☑ план C6 |
| PERF-020 | CODE READY | Быстрые переходы КС: generation/cancel, блокировка до загрузки | ☑ план C6 |
| PERF-023 | CODE READY | Export актов async, отмена, отдельные фазы SQL/build/save | ☑ план C6 |
| PERF-024 | CODE READY | CostSummary: async snapshot + фоновый mapping, publish fence | ☑ план C6 |
| PERF-025 | CODE READY | Раздельные замеры SQL/projection/grouping/formatting/binding себестоимости | ☑ план C6 |
| PERF-027 | CODE READY | ShipmentsPane: фоновой read + projection, generation/cancel | ☑ план C6 |
| PERF-028 | CODE READY | Список ведомостей / строки / остатки по scope/revision без перечитывания | ☑ план C6 |
| PERF-031 | CODE READY | План BuyRows, STATISTICS IO/TIME 2500+/10000, паритет колонок | ☑ план C3 |
| PERF-034 | CODE READY | DirectorView фоновая загрузка, индикатор/отмена/ошибка | ☑ план C6 |
| PERF-035 | CODE READY | DirectorRows: пакетные наборы, запросы не растут линейно | ☑ план C6/C3 |
| PERF-038 | CODE READY | Навигационное дерево: reuse ветвей, тайминг 10/50/200 | ☑ план C6 |
| PERF-039 | CODE READY | Поиск объектов: debounce, по модели/индексу, восстановление раскрытий | ☑ план C6 |
| PERF-040 | CODE READY | Поиск сметы: projection без rebuild, debounce, 25000 строк | ☑ план C6 |
| PERF-041 | CODE READY | SetDocument: CPU/allocations/Dispatcher по фазам | ☑ план C6 |
| PERF-043 | CODE READY | Карточки документов 10/50/200: paging, замер bind | ☑ план C2 |
| PERF-045 | CODE READY | Прокрутка/resize: p50/p95/p99, DPI, 1366×768/1920×1080 | ☑ план C2 |
| PERF-046 | CODE READY | Память: 60 циклов открытия/закрытия, eviction, нет монотонного удержания | ☑ план C2 |
| PERF-048 | CODE READY | SQL-профиль операций: команды, roundtrips, rows/bytes, CPU/waits | ☑ план C3 |
| PERF-049 | CODE READY | Инвентаризация индексов/статистики, фактические планы | ☑ план C3 |
| PERF-055 | CODE READY | 30+/100+ повторов, raw CSV, p50/p95/p99, метаданные окружения | ☑ план C3 |
| PERF-057 | CODE READY | Регрессия: build/test + профильные checks + UI-приёмка Windows | ☑ план C5 |

Кампании: C1 — StartupTrace на живом запуске; C2 — App.Checks layout/retention; C3 — RemoteSqlBench `--runs 100 --diagnostics yes`; C4 — tests/GmcSmeta.Pilot.E2E (nonce-фикстура); C5 — Invoke-LocalAcceptance + полный тестовый прогон; C6 — живой запуск приложения на демо-БД с StartupTrace (кэш, фазы открытия, вкладки, КС, себестоимость, выдачи, директор, поиск, дерево).

Статусы по итогам: DONE — все критерии ID действительно выполнены; иначе остаётся CODE READY с обновлённым остатком, PARTIAL при дефекте, BLOCKED с причиной. Отчёт: один профильный документ, ссылка отсюда.

## Итог кампании perfacc-A — 29.11.2026, брони сняты

Финальные статусы (дублируются в [ACCEPTANCE_TODO](ACCEPTANCE_TODO.md), подробности — [ACCEPTANCE_PERF_20261129](ACCEPTANCE_PERF_20261129.md)):

- **DONE (19)**: PERF-005, 010, 011, 012, 013, 017, 024, 025, 031, 035, 038, 041, 043, 046, 048, 049, 055 — из исходной брони; PERF-051, 052 — расширение брони финальным коммитом (критерии покрыты общими E2E-прогонами, отдельного кода не писалось).
- **CODE READY с уточнённым остатком (13 из брони + 036 вне брони)**: PERF-001 (launcher-фазы/Debug), 009 (живая метка «проверка актуальности»), 015/016 (живое редактирование/паста в WPF), 020/023 (живые переходы КС/экспорт), 027/028 (живые выдачи), 034/036 (живая сводка руководителя), 039/040 (живой поиск), 045 (физические DPI 125/150%), 057 (интерактивная UI-приёмка).
- Регрессий не обнаружено. Сборка Release 0 ошибок; тесты 3110/3110 (вкл. новый source-тест); E2E 40 PASS / 0 FAIL / 6 SKIP; Invoke-LocalAcceptance PASS=10 / FAIL=0.
- Исправленные дефекты харнессов: SET LANGUAGE us_english для STATISTICS IO/TIME (+ source-тест), spec_revisions в E2E-фикстуре v28 (независимо закрыт BUG-приёмкой — конфликт зафиксирован в пользу их надмножества), DispatcherSynchronizationContext для retention-режима App.Checks, PS 5.1 binder в Invoke-LocalAcceptance сериализации.
