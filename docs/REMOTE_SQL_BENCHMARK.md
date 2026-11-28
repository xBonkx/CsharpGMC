# Сетевой бенчмарк GMC-Смета C# — задача №21

**Только на изолированной базе GmcSmetaPilot_*.** Источник исходной методики: bench/README.md. Этот бенчмарк не меняет данные, не создаёт базы и не воспроизводит реальный WPF-ввод ячеек.

## Автоматический read-only прогон (.NET 10)

Укажите в текущей сессии PowerShell временную переменную **GMC_SMETA_SQL_CONNECTION** с доступом к выделенной тестовой базе; не записывайте её значение с паролем в скрипты, консольную историю, Git или журналы. В конце удалите переменную.

Запуск из корня репозитория:

~~~powershell
dotnet run -c Release --project tools/RemoteSqlBench/RemoteSqlBench.csproj -- --runs 100 --sha (git rev-parse HEAD) --topology LAN
# Или определённый тестовый договор:
dotnet run -c Release --project tools/RemoteSqlBench/RemoteSqlBench.csproj -- --runs 100 --sha (git rev-parse HEAD) --topology LAN --contract-id 123
Remove-Item Env:\GMC_SMETA_SQL_CONNECTION
~~~

Измеряются min, p50, p95, max и p99 (только от 100 успешных измерений) после одного прогрева, от 30 до 300 итераций. Автоматические категории:

| Метрика | Что проверяет | Ограничение |
|---|---|---|
| sql_roundtrip_select1 | Латентность пустого запроса по открытому SQL-соединению | Сумма сетевого round-trip + работы драйвера/SQL, не чистый ping |
| sample_300 / sample_2500 / sample_10000 / sample_25000 | SQL SELECT TOP N позиций из одного договора и чтение полученных строк | Это не рендер таблицы; если данных недостаточно, честный SKIP |
| full_estimate_refresh | Вызов реального C# EstimateReader.Load для тестового документа | Содержит несколько SQL-запросов и расчёты, без WPF |
| procurement_load | Вызов SqlServerProcurement.BuyRows | SQL+построение коллекции без WPF |
| switch_contract_a_to_b | Загрузка A, затем B (время обеих загрузок) | ID B в evidence.json |
| guarded_open_warm_pool | Открытие и обязательные проверки SQL, с прогретым пулом | Не cold login |
| selected_totals / stage_distribution / document_compare | Реальные чтения итогов, этапов и сравнения | Сравнение включает оба документа; этапы пропускаются для выбранного дочернего КП |

Для сопоставимых результатов фиксируйте сервер, конфигурацию Wi-Fi/LAN, размер сметы, режим TLS, номер коммита, кол-во проходов, медиану и p95. Лучше 30–50 прогонов, без фоновых rebuild/backup/restore. Колонка **rows** — реальное число прочитанных строк, а не запрошенное. Проверка схемы использует актуальный guarded SQL Open. Режимы с отдельным соединением отмечены в таблице.

## Требуемые отдельные сетевые и WPF замеры

В таблицу ниже внесите реальные результаты с **двух Windows-компьютеров** и выделенного тестового SQL Server. Это **план ручной приёмки, а не выполненный результат**:

| Сценарий | SQL медиана/p95 | Сетевой roundtrip (приближённый) | WPF медиана/p95 | Результат |
|---|---|---|---|---|
| Открытие КП на 300 | — | — | — | Не проводилось |
| Открытие КП на 2500 | — | — | — | Не проводилось |
| Открытие КП на 10000 | — | — | — | Не проводилось |
| Сохранение 1 ячейки (COMMIT) | — | — | — | Не проводилось |
| 100 последовательных изменений | — | — | — | Не проводилось |
| Полное обновление договора | — | — | — | Не проводилось |
| Загрузка закупок | — | — | — | Не проводилось |
| Переключение между договорами | — | — | — | Не проводилось |
| Изменение ячейки → готовые итоги | — | — | — | Не проводилось |

**Методика для записи:** сначала создать/восстановить отдельную синтетическую **GmcSmetaPilot_Bench_*** с 300/2500/10000 позиций (не использовать production и не копировать конфиденциальные записи). Все сохранения измерять на этой одноразовой БД, восстановление/пересоздание — между прогонами. Сравнить серверное время SQL с клиентским Stopwatch и SQL SELECT 1 roundtrip, остаток включает сериализацию и WPF. Для WPF измерять через Stopwatch вокруг получения данных, Dispatcher/LayoutUpdated и пересчёта итогов; избегать утверждения, что разность этих таймеров равна чистой задержке сети. При 100 изменениях проверять значения и ревизии после каждого успешного COMMIT. При сетевой ошибке с неизвестным исходом **остановить серию**, не повторять запись автоматически: восстановить соединение, перечитать состояние и ревизию.

Запись, автоматический COMMIT benchmark, измерение WPF и сценарии двух клиентов **не реализованы в read-only harness**, потому что нужен живой выделенный SQL Server и контроль безопасности изменений. Не подменять их цифрами из SELECT TOP N.

## 28.11.2026 — воспроизводимые доказательства (PERF-054/055/058/059)

Каждый запуск создаёт уникальную папку `tools/RemoteSqlBench/out/<run-id>` (или `--out <folder>`). `evidence.json` содержит SHA, топологию, runtime/OS/CPU, версию SQL и драйвера, схему, размер, Release/Debug, количество повторов, статусы и сводку. `samples.csv` содержит каждый замер, включая прогрев (iteration 0) и ошибку. Не публиковать сырые файлы без проверки. Не передавать секреты в аргументах. SHA задаётся оператором из чистого checkout; `UNSPECIFIED` не годится для сравнения версий. `--topology LAN|WIFI|LOCAL|VPN|SLOW|OTHER` задаётся оператором. Allocation bytes относятся только к текущему потоку, не пиковая память процесса. p99 при менее 100 измерениях отсутствует. Ошибка останавливает прогон, неполный или изменившийся revision результат не считается успешным; exit 0 — неизменная ревизия и завершённые доступные сценарии, 4 — изменившаяся ревизия, 5 — ошибка. SKIP при недостатке строк не является PASS. Не сравнивать разные ID/размеры, топологии, конфигурации и режимы кэша. WPF, Python baseline, cold process, SQL IO/TIME и двухклиентские записи остаются отдельными проверками. Числа будут только после реального запуска.

## Дополнительная SQL-диагностика (PERF-031/049)

Добавьте `--diagnostics yes` к предыдущей команде. По умолчанию выключено. После таймеров отдельные непулируемые guarded подключения на Pilot собирают `indexes.json` (порядок ключей, INCLUDE, фильтры, disabled/unique), `statistics.json` (столбцы и STATS_DATE), `procurement-parity.json` (сверка 4 видов движений, даты последней выдачи, порядка и производного статуса с независимым коррелированным запросом), `procurement-io-time.txt` (STATISTICS IO/TIME, английские сообщения) и `procurement-actual-plan.sqlplan` (STATISTICS XML, фактическое исполнение того же BuyRowsQuery). Отсутствие SHOWPLAN/доступа к метаданным или локализованные сообщения не заменять нулями: диагностика отмечается INCOMPLETE/UNAVAILABLE, при стабильной ревизии exit 6. Метаданные могут быть неполны из-за прав. План и фильтры индексов могут содержать внутренние имена и параметры — хранить локально, проверить перед передачей. Никаких CREATE INDEX, UPDATE STATISTICS, очистки кэша, NOLOCK или DDL. Не считать сверку движений полным Python-паритетом всех полей. План нужно вручную оценить по seek/scan/sort/lookup, estimates/actual rows и logical reads на одинаковых размерах; на основании одного запуска не добавлять индексы. Для прав SHOWPLAN обращаться только к администратору тестовой базы.

## WPF layout evidence (PERF-042/043/044), отдельный Windows-прогон

```powershell
dotnet run -c Release --project tests/GmcSmeta.App.Checks/GmcSmeta.App.Checks.csproj -- --layout-performance-check tools/RemoteSqlBench/out/layout
```

Синтетические данные, реальный App.xaml, compiled Materials/Summary panels, общая DocumentTable с 30 колонками и одной frozen; 300/2500/10000/25000 строк, верх и переход к последней строке/колонке. CSV фиксирует размер, фактическое DPI, время публикации/прокрутки с Dispatcher/layout, созданные DataGridRow/DataGridCell и visual nodes. Проверяется ограниченное число строк и достижение нужного контейнера. Карточки MainWindow на 10/50/200 документах + карточка создания измеряются отдельно (MEASURED, не PASS виртуализации), обычный WrapPanel сохранён. Никакого подключения к SQL, нет реальных данных; окна открываются только в явном режиме. Время одного прохода не p95/p99, не сравнивать с SQL-таймерами. Не заменяет полную матрицу WPF на реальных сметах, grouped grids, редактирование, физические DPI, длительные серии, profiler/GC и проверку карточек. Результаты не получены до реального запуска.

## 28.11.2026 — запуск и локальная регрессия

- `guarded_open_nonpooled` — отдельный свежий физический login с `Pooling=false`, обязательные guard/schema checks сохранены. Не означает холодный SQL cache или запуск нового процесса; `guarded_open_warm_pool` — обычный пул. Ни один общий пул/кэш не очищается.
- `GMC_SMETA_STARTUP_TRACE_DIR` по-прежнему включает локальные CSV фаз запуска. Теперь отдельно фиксируются ожидание старого процесса, branding, регистрация interaction handlers, декодирование иконки, непосредственный `InitializeComponent`, настройки, shell setup, PTO menu, readiness и dispatcher idle. Последний — не доказательство физического кадра. Загрузка ресурсов до `OnStartup`, JIT/антивирус/диск и R2R требуют внешнего Windows-профилирования; синхронное журналирование само может влиять на замеры, использовать одинаковый режим трассировки.
- Запуск из корня: `powershell -NoProfile -File tools/local-qa/Invoke-LocalAcceptance.ps1 -LayoutPerformance`. Без флага запускаются сборка/тесты, WPF offline, KS/PDF/XLSX export, закупки и КП/выдачи; с флагом также измерения layout. Для этих запусков временно снимаются известные SQL/export environment variables, после восстанавливаются; optional SQL identity probe отдельно и только с явными аргументами Pilot. FAIL возвращает exit 1, SKIP не подтверждает приёмку. Все логи локальны, проверять перед публикацией. Не запускает SQL write workflow, Python baseline или ручные испытания. Релиз не публикуется.

## SQL contention and cancellation (PERF-010/051/056)

Existing `tests/GmcSmeta.Pilot.E2E` now registers four additional real-SQL cases, exclusively on the runner-created nonce/provenance-verified disposable `GmcSmetaPilot_E2E_*`. Three distinct positions across two documents race with the same global revision: exactly one write may commit, rejected clients must leave their rows intact and see the winning revision, fresh connection verifies persistence. A write to another document must invalidate the old procurement snapshot without changing its quantities/statuses. A separate nonpooled session's revision read is blocked by a held revision-row lock and cancellation must complete before rollback; no DB isolation change, only session isolation in the test. Five pre-cancelled public read APIs are checked. Existing fixture creation/cleanup requires the runner's explicit admin configuration; do not point it at office/production or run with inherited credentials. Run only on a dedicated test instance after verifying runner safety and environment, never from the read-only benchmark. Timing samples include full client elapsed time, not measured server lock waits. Physical network disconnect, real 3-role WPF sessions, lock-wait DMV profiling and complete mid-flight projection cancellation are still pending. No new per-document revision scheme: the global revision remains authoritative because older writers and linked-document mutations can invalidate any narrower cache. A compatible future design must audit all writers and dependency graph, version/migrate explicitly and retain global fallback until mixed-version acceptance. No DDL or production guard changes in this block.

## 28.11.2026 — офлайн-сравнение SQL/client evidence (PERF-053/054/055/058/059)

Формат evidence v2: 30–300 измерений + отдельный прогрев; явные псевдонимы синтетического набора и неизменного профиля оборудования/сети/настроек (не имя ПК, не секрет). Добавлены SLOW, FAIL/TIMEOUT/CANCELLED, снимки process CPU/private bytes/working set/managed bytes до и после каждого сценария вне его таймера. Это не пики и не доказательство retention. Исходные samples.csv и JSON сохраняются. Пример двух чистых Release checkout одного набора и профиля:

```powershell
dotnet run -c Release --project tools/RemoteSqlBench/RemoteSqlBench.csproj -- --runs 100 --sha (git rev-parse HEAD) --topology LAN --dataset seed-v1 --machine-profile pc-lan-1 --contract-id 123
# Повторить на новой версии с теми же данными, ID, машиной и настройками.
dotnet run -c Release --project tools/RemoteSqlBench/RemoteSqlBench.csproj -- --compare before/evidence.json after/evidence.json --max-regression-pct 10 --report comparison.json
```

Сравнение работает офлайн, без SQL-подключения; v1 и неполные метаданные отвергаются. Сверяются полные SHA (оба должны быть указаны), distinct run IDs, labels, contract/size, profile, runtime/OS/CPU, schema/SQL server/client version, topology, cache mode, build and run count. Метки задаёт оператор, они не доказывают идентичность содержимого БД. Проверяются точные номера и статусы исходных итераций, пересчитываются сводки, сверяются rows и second contract ID; нулевой baseline с ненулевым результатом не даёт процента. p99 только при 100+ итерациях. Матрица JSON содержит оба run ID (ссылки на папки сырых данных), оба SHA, условия, p50/p95/p99, allocation и process snapshots; порог 10% по умолчанию, регулируемый, это индикатор замедления, не статистический тест. Exit 0 — все сравниваемые сценарии MEASURED без превышения порога; 2 — неверные/несопоставимые входы или существующий report; 3 — хотя бы один SKIP/INCOMPLETE; 4 — превышение порога хотя бы по одному latency percentile. REGRESSION имеет приоритет над неполнотой. Отчёт не перезаписывается. WPF stalls, число SQL-команд, запись/COMMIT, полный correctness, retention, Python baseline и реальная статистическая приёмка остаются отдельно; ни один SQL read-only результат не считается полной приёмкой. Не заявлять реальные цифры без прогона.


## Полная проверка SQL-реализации (PERF-010/031/036/049/052)

- `--diagnostics yes` дополнительно сравнивает все mapped BuyRow поля/флаги и порядок с независимым коррелированным SQL, не только движения. Пустая выборка, изменение даты/ревизии или несовпадение не PASS. `procurement-all-columns.json` содержит только счётчики.
- Отдельные `index-usage.json` (seek/scan/lookup/update с момента доступного DMV сбора) и `statistics-details.json` (rows/sample/modifications); NULL/отсутствие данных не означает нулевую нагрузку. Нужны разрешения тестовой базы; изменения индексов только после анализа actual plans и сопоставимого write benchmark, не автоматические.
- Для KP и договоров/ДС, на 10/50/200 активных документах, отдельные director-*-parity.json, -io-time.txt и -plans.json (три фактических плана production batch, raw XML в JSON). При нехватке выбранного scope — INCOMPLETE без фиктивного файла. Сравнение входов агрегатов по мультимножествам, включая повторы и три result sets, независимо от SQL порядка; финальные финансовые поля проверяются существующими PilotFinanceBatchCases/PilotDirectorMovementCases. Запустить на репрезентативных отдельных Pilot наборах; не публиковать raw plans без проверки. Не подключать даже разрешённый приложению Cutover.
- PERF-010: безопасный совместимый вариант — сохраняется глобальная meta.rev, так как старый Python может не знать локальные ревизии. Не игнорировать чужую правку и не повторять запись. Legacy-only fixture изменяет только meta.rev и проверяет отказ stale-write, invalidation закупок/руководителя и reuse после подтверждения.
- PERF-052: schema approval не кэшируется, проверка выполняется при каждом logical open, независимо от пула. В synthetic E2E проверяется отклонение временного неверного schema_version после успешного pooled open, sync/async и nonpooled, обязательный QUOTED_IDENTIFIER, восстановление и неизменная business revision. Реальные v31/v32 preview/manifest guards сохранены, контроль профилей на выделенных копиях при приёмке. Opt-in trace различает transport и validation; не очищать серверный кэш или рабочие пулы.
- Все новые Pilot cases запускаются обычным `dotnet run -c Release --project tests/GmcSmeta.Pilot.E2E` по существующей инструкции создания nonce-owned disposable fixture; fault injection только в принадлежащей runner базе. Фактических результатов в этом коммите нет.


## SQL operation profile and offline matrix (PERF-048/051/055/057/059)

- `--diagnostics yes` additionally creates `sql-operation-profile.json`: separate guarded nonpooled session, provider command count (diagnostic events, null when unavailable), roundtrips, actual rows/result sets, bytes, provider execution and network-server-inclusive time, independent observer DMV CPU/logical/physical reads/writes and wait deltas. Observer permissions may be missing; NULL/INCOMPLETE are not zero. No SQL text, parameters, identity or credentials in profile. Not a clean latency benchmark; use existing timed runs separately. Existing STATISTICS IO/TIME and actual plans remain separate; WPF uses existing phase trace. No server cache/pool clearing.
- StartupTrace adds SaveCell revision lock, serializable transaction (includes disposal/rollback) and COMMIT durations; these are opt-in client phases, not server lock duration. Pilot blocked revision scenario independently attempts DMV wait observation on the owned fixture, then still requires cancellation while the blocker holds the lock. Missing visibility is an explicit SKIP.
- `samples.csv` now records full round-trip precision, cache_mode per case, and keeps warmup/errors. p99 absent below 100 successful samples. `--compare` remains compatible with evidence format 2.
- Run `Invoke-LocalAcceptance.ps1 -LayoutPerformance -PilotE2E -RequireAutomatedAcceptance` only with explicitly configured **isolated** GMC_PILOT_E2E_ADMIN and GMC_PILOT_E2E_EXPECTED_SERVER; existing E2E creates/drops its nonce-owned fixture. Does not accept partial or filtered E2E as full regression. Exit 1 failure, 2 incomplete strict gate; manual `Finalize-ManualAcceptance.ps1 -RunDirectory ... -RequireFullAcceptance` likewise requires evidence and clean SHA. Existing intentional synthetic-only SKIPs mean strict gate stays incomplete until replaced by actual full-schema acceptance; no release approval is implied.
- Offline `python tools/local-qa/performance_matrix.py manifest.json matrix.json`: manifest has pairs (scenario unique; before/after relative metadata JSON) or unmeasured rows {scenario,status:OPEN/PARTIAL/BLOCKED,reason}. Metadata keys: sha (exact 40/64 hex), scenario, client (CSharp/Python), dataset, machine_profile, topology, cache, build, runtime, os, sql_version, schema, rows, contracts, dpi, resolution, runs (30..300), required_metrics, correctness (PASS/FAIL/UNVERIFIED), correctness_evidence (relative independent proof), raw (relative CSV). CSV header: iteration,status,elapsed_ms,sql_count,ui_stall_ms,private_bytes,working_set_bytes,managed_bytes,cpu_ms,retained_bytes,bytes_received. Iteration 0 WARMUP, remaining OK/FAIL/TIMEOUT/CANCELLED. Missing values blank, never invent zeros. Each metric required by scenario must have all samples; declare non-applicable fields explicitly by omitting from required_metrics. Matrix comparisons are same-client/same-cache/same-profile only; separate rows for C# and Python, cold/warm, write/read, sizes and topologies. Raw and proof file SHA256 recorded. Matrix DONE means supplied row comparison meets declared threshold and operator-provided correctness evidence, not release acceptance; no automatic attestation of evidence content. `--max-regression-pct` default 10; output create-new, exit 3 incomplete/regression, 2 invalid. External producer integration still PERF-053/054/058. Tests: `python -m unittest discover -s tools/local-qa -p test_performance_matrix.py`.
