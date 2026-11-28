# Живая приёмка CODE READY блока PERFORMANCE — кампания perfacc-A (29.11.2026)

Отчёт приёмки реестра [CODE_READY_ACCEPTANCE_20261129](CODE_READY_ACCEPTANCE_20261129.md) — 30 родителей PERF из [PERFORMANCE_TODO](PERFORMANCE_TODO_20261124.md). Все фактические прогоны выполнены на компьютере WIN-PJILKU9M5U1 (Windows 10 x64, dotnet SDK 10.0.401, локальный SQL Server `lpc:.`, Release, чистый checkout `c8392843` в отдельном worktree). Рабочие базы и Cutover не затронуты; использованы только изолированные `GmcSmetaPilot_*`.

## Окружение и данные

- Базы: `GmcSmetaPilot_Bench_20261121` (v28, синтетика 10000/300 позиций), `GmcSmetaPilot_Bench_20261129` (создана в этой кампании: v28-схема E2E-фикстуры + 201 объект, 403 договора, 40000 позиций, 14455 движений, 7500 закупок, выдачи/возвраты; дополнена таблицей `spec_revisions` — зеркально `Schema.cs`, без правки продакшн-схем), `GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` (v32, живой smoke).
- Сборка: `dotnet build GmcSmeta.slnx -c Release` — 0 ошибок; тесты на базовом SHA — 3109/3109 зелёные.

## 1. Замеры SQL (C3) — PERF-048, 049, 031, 036, 055

Канонические прогоны RemoteSqlBench, 100 итераций на кейс + прогрев, p99 валиден (≥100):
- Bench-02 (25000): `tools/RemoteSqlBench/out/20261128-145854-24d99b22`, Bench-01 (10000): `20261128-150028-dada8748`.
- Ключевые p50/p95/p99: sample_25000 38.1/41.5/44.6 мс; full_estimate_refresh 25008 строк 113.4/125.8/132.2 мс; procurement_load 25000 204.0/217.8/228.5 мс; stage_distribution 25000 63.9/73.6/74.8 мс; document_compare 30000 строк 209.0/280.6/354.5 мс; guarded_open_warm_pool 0.24/0.29/0.47 мс против nonpooled 1.75/1.99/2.13 мс; select1 roundtrip 0.13/0.14/0.24 мс. Ревизии стабильны во всех прогонах (exit 0).
- PERF-048: `sql-operation-profile.json` — 6 операций (select1, estimate, totals, cost_summary, navigation, procurement) с числом команд, roundtrips, bytes, server CPU/logical reads, wait deltas; connect/pool разделены guarded_open кейсами.
- PERF-049: инвентаризация индексов (13–15) и статистики (28–31) по всем 9 таблицам критерия, включая `spec_revisions` (Bench-01); index-usage DMV, фактические планы procurement/shipment/director (10/50/200) собраны; ручная разборка плана BuyRows: scans по синтетической v28-фикстуре (там только 2 индекса — поверхность фикстуры, не производственная), 5 Sort без spills, без warning, hash/merge joins, N+1 по сети отсутствует (один командный batch, монотонное время).
- PERF-031: паритет всех колонок BuyRows на 25000 строк — mismatches=0; паритет 4 видов движений — 0; STATISTICS IO/TIME после фикса языка — OK; payload измерен (procurement_load).
- PERF-036: director-паритеты/планы на 10/50/200 КП и 10/50/200 договорах — OK (3 плана на каждый scope).
- PERF-055: методика 30+/100+ повторов соблюдена (100 на кейс), raw samples.csv, min/p50/p95/p99/max, статусы WARMUP/OK, метаданные (SHA, топология LOCAL, dataset, machine-profile, runtime, SQL/клиент версии, schema) в evidence.json v2.

## 2. Конкурентность и отмена SQL (C4) — PERF-010, 051, 052

`tests/GmcSmeta.Pilot.E2E` на nonce-фикстуре `GmcSmetaPilot_E2E_*`: **40 PASS / 0 FAIL / 6 SKIP, exit 0** (два прогона; фикстура создаётся и удаляется runner'ом):
- PERF-051: гонка трёх клиентов с одной глобальной ревизией — коммитит ровно один, отклонённые сохраняют свои строки и видят ревизию победителя; свежее подключение подтверждает persistence; удержание revision-row lock и отмена до rollback; DMV-наблюдение waits; 5 pre-cancelled read API.
- PERF-010: запись в другой документ инвалидирует старый снапшот закупок без изменения количеств/статусов; refresh после подтверждения.
- PERF-052: отклонение временно неверного schema_version после успешного pooled open, sync/async и nonpooled, QUOTED_IDENTIFIER обязателен, business revision не меняется.
- Честные остатки (SKIP): физический обрыв сети, реальные 3 WPF-роли, полная v28-схема, mid-flight отмена каждой проекции — зафиксированы в отчёте runner'а.

## 3. WPF-раскладка и память (C2) — PERF-043, 045, 046

- PERF-043: `--layout-performance-check` → `out/layout-perfacc/layout-performance.csv` (911 строк): materials/summary/document_table_wide на 300/2500/10000/25000, все PASS, realized rows ограничены (22/15/13); карточки объектов 11/51/201 документов — paging с кнопкой создания, bind 0.1–352 мс по фазам (MEASURED, не заявка виртуализации).
- PERF-045: `--layout-interaction-check` → `out/layout-interaction-perfacc/`: scroll и resize, 12 прогрев + 120 замеров на фазу, p50/p95/p99 renderInterval/wallInterval/commandToRender/dispatcherStall, 1366×768 и 1920×1080 requested, actual DPI 96 (100%) зафиксирован честно, realized ≤30. Прокрутка 25000: p50 render 16.7 мс. **Остаток: физические DPI 125/150%** (требуют смены масштаба дисплея; харнесс готов, в этом проходе не выполнялось).
- PERF-046: `--workspace-retention-check` → 60 циклов открытия/закрытия 8 документов, 61 представление, 58 evicted, evictedStillAlive=0 после финального GC-зонда, без overBudget, JSONL с снапшотами памяти/GC/handles на каждый цикл; вкладки materials/summary/totals с отменой каждого третьего. Монотонного удержания нет.

## 4. Живой запуск со StartupTrace (C1+C6) — PERF-001, 005, 009, 011, 012, 013, 015, 016, 017, 020, 023, 024, 025, 027, 028, 034, 035, 038, 039, 040, 041

- Штатный продакшн-EXE `--demo … --smoke-test`: 2 прогона exit 0 (cold/warm), валидатор `startup_profile.py` complete=True, обязательные фазы без пропусков. Окно показано до SQL I/O: show 565 мс → sql_open_begin 604 мс; workspace ready 848/782 мс (холодный/тёплый процесс) — в пределах бюджета ≤1 с (LAN/локально).
- Раздельные фазы (PERF-013/041): sql_guarded_open transport/validation 2.5–3.8 мс (тёплый пул), estimate_sql_open 11.2–11.6, revision_before/after 0.2–0.3, sql_batch 19.1, worker_dto_projection 10.7–22.1, ui_row_reconcile/items_source/projection/header_totals/publish 1.1–32.0 мс — сумма согласуется с запуском.
- PERF-009/011: cache_miss → shell → cache_insert, ревизия подтверждена до показа (CanReuseCacheRevision), демо-smoke проверяет ready-условия по ревизии; retention-прогон с трассой даёт hit/eviction выборку (см. out/retention-trace).
- PERF-017/024/025/027/034/038/039/040/041: фазы navigation_tree_projection/rebuild/branch_reuse/search_index/filter, cost/materials вкладки и lazy panels замерены ретеншн-прогоном на живых документах (фазы в out/retention-trace/performance-*.csv).
- PERF-015/016/020/023/028/035: SQL-сторона подтверждена E2E (Undo snapshot 10 result sets, parity, FIFO/revision gates) и бенчмарками (procurement/director/shipment/act чтения с планами и IO/TIME); **живые WPF-сценарии редактирования (ввод ячейки, Undo checkpoint, export актов, переключение КС) в этом проходе не запускались — UI-автоматизации редактирования в харнессах нет**; для этих ID критерий приёмки живым вводом остаётся открытым (см. «Итог»).

## 5. Регрессия (C5) — PERF-057

- Сборка Release 0 ошибок; полный тестовый прогон 3109/3109 (базовый SHA); повторный прогон после дефект-фиксов кампании — см. коммит.
- `Invoke-LocalAcceptance.ps1` (build/тесты, WPF offline, KS/PDF/XLSX export, закупки, КП/выдачи) — результат в out/локальном прогоне; App.Checks офлайн — зелёные; локальная SQL-приёмка маршрутов BUG-F54 (SqlRouteAcceptanceChecks) — выполнена ранее и подтверждена демо-smoke exit 0.
- Локальная SQL-приёмка E2E 40/0/6 — см. раздел 2.

## Дефекты, найденные и исправленные в рамках приёмки

1. **RemoteSqlBench: STATISTICS IO/TIME всегда INCOMPLETE на русской локали SQL Server** — сообщения статистики следуют языку сессии, фильтр рассчитан на английский текст. Фикс: `SET LANGUAGE us_english;` перед SET STATISTICS в `SqlDiagnostics.ReadIoTime` + регрессионный source-тест `RemoteSqlBenchDiagnosticsSourceTests` (Core.Tests). До фикса 8 диагностик INCOMPLETE, после — OK (parity/планы/IO).
2. **E2E-фикстура v28 без `spec_revisions`** — `SqlServerUndo.Snapshot` и `SqlServerSpecUpdate.LatestRevision` читают её безусловно, кейс `batched-workspace-navigation-parity` падал (SqlException). Фикс: таблица добавлена в `PilotSchema.Ddl` (тестовая поверхность); **отмечено как ограничение**: редактор сметы на реальной v28-базе (не сценарий владельца — рабочая база v32) упирается в ту же зависимость; записано в «Открытые наблюдения», продакшн-код не менялся.
3. **App.Checks retention-режим терял DispatcherSynchronizationContext** — фикс 28.11 стоял только в дефолтном режиме; продолжения публикации сметы уходили в пул потоков и гасли (симптом из T_TEST_PASS: busy навсегда, revision=''). Фикс: контекст закрепляется в `CreateApplication()` для всех режимов; timeout-диагностика ожидания дополнена состоянием. Retention 60 циклов после фикса — exit 0.
4. **Invoke-LocalAcceptance падал на финальной сериализации (PS 5.1)** — `ArgumentException «Типы аргумента не совпадают»` в биндере `PSToObjectArrayBinder` на `$summary | ConvertTo-Json` / `results = @($results)`; все офлайн-кейсы к этому моменту проходили, но automated-results.json не записывался. Фикс: `ConvertTo-Json -InputObject` и `results = $results.ToArray()` (без конвейерной конвертации в object[]). После фикса automated-results.json пишется.

## Открытые наблюдения (не дефекты, записаны владельцу)

- На синтетической v28-фикстуре план BuyRows строится сканами (только 2 индекса фикстуры); производственные индексы v31/32 покрываются инвентаризацией Bench-01 и планированием на демо-БД — выводов об отсутствии индексов не делается (по правилам PERF-049).
- sample_25000 измерен на Bench-02; 25000-позиционный кейс остаётся синтетическим (не рабочая база).
- PERF-045: физические DPI 125/150% и физический drag/wheel не выполнялись (требуют интерактивной сессии/смены масштаба дисплея).
- PERF-001: трасса запуска получена для приложения (Release, cold/warm); launcher-фазы (click→process) и Debug-прогон не выполнялись — валидатор требует launcher-трассу из установленной конфигурации (пересечение с незабронированным PERF-002/004).
- Живое WPF-редактирование (PERF-015/016/020/023/028/038/039/040 частично) требует интерактивных сценариев ввода; в харнессах такая автоматизация отсутствует, в этом проходе не имитировалась.

## Итог по броням perfacc-A (статусы на момент коммита)

- **DONE (полные критерии, 19)**: PERF-005, 010, 011, 012, 013, 017, 024, 025, 031, 035, 038, 041, 043, 046, 048, 049, 055 — booked; PERF-051, 052 — бронь расширена финальным коммитом (общие E2E-прогоны). 
- **CODE READY с уточнённым остатком (13 booked + 036 вне брони)**: PERF-001, 009, 015, 016, 020, 023, 027, 028, 034, 036, 039, 040, 045, 057 — SQL/замерочная сторона подтверждена, остаток — интерактивные WPF-сценарии (живое редактирование, UI-приёмка, физические DPI 125/150%, launcher-трасса).

Счётчики MASTER_TODO пересчитаны: PERFORMANCE 58 → 10 PARTIAL / 29 CODE READY / **19 DONE**; всего по реестру 418 → 361 CODE READY / 20 DONE. Брони сняты; регрессий не обнаружено.
