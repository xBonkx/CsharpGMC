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

Измеряются min, p50, p95, max и p99 (только от 100 успешных измерений) после одного прогрева, от 20 до 300 итераций. Автоматические категории:

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

Каждый запуск создаёт уникальную папку `tools/RemoteSqlBench/out/<run-id>` (или `--out <folder>`). `evidence.json` содержит SHA, топологию, runtime/OS/CPU, версию SQL и драйвера, схему, размер, Release/Debug, количество повторов, статусы и сводку. `samples.csv` содержит каждый замер, включая прогрев (iteration 0) и ошибку. Не публиковать сырые файлы без проверки. Не передавать секреты в аргументах. SHA задаётся оператором из чистого checkout; `UNSPECIFIED` не годится для сравнения версий. `--topology LAN|WIFI|LOCAL|VPN|OTHER` задаётся оператором. Allocation bytes относятся только к текущему потоку, не пиковая память процесса. p99 при менее 100 измерениях отсутствует. Ошибка останавливает прогон, неполный или изменившийся revision результат не считается успешным; exit 0 — неизменная ревизия и завершённые доступные сценарии, 4 — изменившаяся ревизия, 5 — ошибка. SKIP при недостатке строк не является PASS. Не сравнивать разные ID/размеры, топологии, конфигурации и режимы кэша. WPF, Python baseline, cold process, SQL IO/TIME и двухклиентские записи остаются отдельными проверками. Числа будут только после реального запуска.

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
