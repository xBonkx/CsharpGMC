# Аудит размера исходников и декомпозиции — 26.11.2026

Цель: не повторять монолитные файлы Python; улучшить сопровождаемость и снизить конфликты параллельных агентов, не изменяя функциональность. Это самостоятельная архитектурная очередь, не замена приоритету переноса Python/SQL. Правило обязательно для всех агентов через [AGENTS.md](../AGENTS.md).

## Подтверждённый исходный срез

Сравнение выполнено по наиболее крупным файлам по размеру в байтах; физические строки подсчитаны из содержимого, включая комментарии и пустые строки. Не полный подсчёт всех файлов. C# — дерево [4f944fdf](https://github.com/xBonkx/CsharpGMC/tree/4f944fdf9f6955e3a1114e5c03c811db7c7db08b); Python — дерево [aa6b34dc](https://github.com/xBonkx/gmcsmeta/tree/aa6b34dcf0314df93086a297f01d549ff58cf705). Перед реализацией перепроверить свежий main, поскольку файлы меняются параллельно.

| C# (относительно src/) | Строк |
|---|---:|
| GmcSmeta.App/ActsEditorView.xaml.cs | 2384 |
| GmcSmeta.App/EstimateView.xaml | 2308 |
| GmcSmeta.App/MainWindow.xaml.cs | 1933 |
| GmcSmeta.App/EstimateView.Materials.cs | 1717 |
| GmcSmeta.App/App.xaml | 1559 |
| GmcSmeta.Core/Db/SqlServerStages.cs | 1319 |
| GmcSmeta.Core/Db/SqlServerActs.cs | 1257 |
| GmcSmeta.Core/Db/SqlServerWorkspace.cs | 1163 |

Python: smeta/ui/kp_editor.py — 3961, ks_view.py — 2356, proc_view.py — 1732, main_window.py — 1625, common.py — 1624, cost_view.py — 1493, smeta/db.py — 1357, smeta/importer.py — 1146. В C# семейство EstimateView состоит из 24 файлов (включая XAML); наличие partial само по себе не доказывает архитектурную декомпозицию. Нельзя утверждать отсутствие файлов >4000 до полного подсчёта.

## Пороговые правила

До 500 — цель; 501–1000 — допустимо; 1001–1500 — проверить ответственность; 1501–2000 — кандидат; 2001–4000 — приоритетный рефакторинг; >4000 — не допускать без явного согласования владельца и обоснования. Порог — сигнал, а не повод механически нарезать код. Считать отдельно физические файлы и суммарный размер partial-класса; исключать generated/vendor/bin/obj с объяснением. Не уплотнять код ради метрики.

## Задачи

### ARCH-SIZE-01 (P1) — NOT STARTED
Полный инвентарь рукописных .cs/.xaml: количество, строки, топ-30, диапазоны, файлы >1000/1500/2000/4000, агрегирование по partial-типам (namespace + type), зависимости, длинные методы, смешение UI/расчётов/SQL. Отдельно src/tests, сохранить SHA, метод подсчёта, исключения и карту пересечений с действующими TODO. Приёмка: воспроизводимый отчёт с точными путями и планом рефакторинга.

### ARCH-SIZE-02 (P1) — NOT STARTED
ActsEditorView.xaml.cs: инвентаризировать обработчики, состояние, расчёты и операции подписания/экспорта; выделять независимые компоненты, а не просто partial. Приёмка: тесты КС-2/КС-3, скидки, ручные суммы, НДС, частичные закрытия, подпись, выбор актов и экспорт; прежнее поведение и суммы.

### ARCH-SIZE-03 (P1) — NOT STARTED
EstimateView.xaml и семейство EstimateView: проверить визуальные области, ресурсы, binding/имена элементов и границы состояния. Выделить логически цельные UserControl/ResourceDictionary/команды там, где это безопасно; оценить общий partial-класс, зависимости и разделение UI, расчётов, импорта, сохранения. Приёмка: сборка, WPF GUI 100/125/150% DPI, таблицы и выделение, меню, скрытие столбцов, редактирование, отмена, очереди, паритет Python.

### ARCH-SIZE-04 (P1) — NOT STARTED
MainWindow.xaml.cs: оценить навигацию, загрузку, команды, жизненный цикл, подписки и работу с окнами; выносить самостоятельные координаторы/сервисы, не менять время жизни ресурсов и порядок инициализации. Приёмка: запуск, переключение договоров/объектов, импорт, закрытие, отсутствие утечек/дублирования обработчиков.

### ARCH-SIZE-05 (P1) — NOT STARTED
EstimateView.Materials.cs: выделить независимые операции и модели материалов, поискать смешение диалогов, UI и доменной логики. Приёмка: добавление/редактирование/удаление, формулы, вставка, группировка, отмена, сохранение, точный паритет.

### ARCH-SIZE-06 (P2) — NOT STARTED
App.xaml: тематические словари ресурсов для таблиц, меню, кнопок, диалогов и прочего после карты зависимостей StaticResource/DynamicResource; не менять ключи, порядок разрешения, исходные иконки и визуальные состояния. Приёмка: сборка, запуск и сравнение визуального паритета.

### ARCH-SIZE-07 (P2) — NOT STARTED
SqlServerStages.cs, SqlServerActs.cs, SqlServerWorkspace.cs: проверить разделение запросов, маппинга, валидации, транзакций, денежной логики. Выделять компоненты без изменения SQL-контрактов, схемы, границ транзакций и защит. Приёмка: тесты SQL только на GmcSmetaPilot_*, двухклиентные сценарии, rollback, прежние суммы/остатки и защита рабочей БД.

### ARCH-SIZE-08 (P2) — NOT STARTED
Добавить воспроизводимый локальный скрипт отчёта размеров и проверку роста для изменённых файлов; существующий долг фиксировать baseline, не блокировать исправления из-за прежнего размера, не разрешать необоснованное разрастание. Сначала предупреждения, затем согласованный порог/исключения; не зависеть от платных GitHub Actions. Приёмка: тесты скрипта на границах диапазонов, generated/partial и понятный отчёт до/после.

## Общая приёмка и ограничения

- Один небольший логически завершённый блок за коммит; перед началом обновить main, зарегистрировать ID/агента и не брать занятые задачи.
- Сохранить публичные контракты, формулы, округление, UI, точные Python-иконки, очередь сохранения, транзакции и производительность; никаких функциональных изменений под видом рефакторинга. Только SQL Server, рабочая БД и Cutover недоступны для изменений.
- Фиксировать число файлов, физические строки до/после, размер logical partial-класса, git additions/deletions и фактически выполненные проверки. dotnet build GmcSmeta.slnx, dotnet test GmcSmeta.slnx, соответствующие Windows/WPF/SQL Pilot и экспортные проверки по действующим acceptance. DONE только после фактической приёмки.
- При обнаружении пересечения с существующим TODO связать ID и не дублировать работу; не менять исторические 400 ID без содержательной сверки.

Регистрация 26.11.2026: 8 NOT STARTED; правила внесены в AGENTS.md. Код, БД и сборка не изменялись; реализация и приёмка впереди.

- 26.11.2026, PERF-019 / ARCH-SIZE-02: ActsEditorView.xaml.cs 2466→2463, новый ActsEditorView.QuantityReferences.cs 28, ActQuantityFormulas.cs 252→228, новый ActQuantityReferenceIndex.cs 54 строк. Выделен самостоятельный тестируемый индекс ссылок; WPF-адаптер вынесен без расширения большого файла. Полная декомпозиция редактора остаётся NOT STARTED; сборка/тесты не запускались.

- 26.11.2026, PERF-019 / ARCH-SIZE-02: ActsEditorView.xaml.cs 2463→2436, ActsEditorView.QuantityReferences.cs 28→62, новый KsReferenceHighlights.cs 50, Program.cs (App.Checks) 581→633 строк. Выделен компонент KsReferenceHighlights и перенесены обработчики ссылок; крупный редактор уменьшен, рост тестового runner — один проверочный сценарий. Общая декомпозиция остаётся отдельной задачей; сборка/тесты не запускались.

- 26.11.2026, PERF-019 / ARCH-SIZE-02: ActsEditorView.xaml.cs 2436→2345, новый KsRowVm.cs 104, Program.cs (App.Checks) 634→635, новый KsRowNotificationChecks.cs 70 строк. KsRowVm извлечён как самостоятельная модель, проверка вынесена из runner. Общая декомпозиция редактора остаётся отдельной задачей; сборка/тесты не запускались.

- 26.11.2026, PERF-026 / ARCH-SIZE: EstimateView.CostSummary.cs 1359→1364, CostMissingPriceIndex.cs 41→52, CostMissingPriceIndexTests.cs 44→66 строк. Встроенная Сводная переиспользует существующий Core-индекс; рост крупного файла — только интеграция/lookup, отдельная декомпозиция остаётся в архитектурной очереди. Сборка/тесты не запускались.

- 26.11.2026, PERF-026 / ARCH-SIZE: EstimateView.CostSummary.cs 1364→1350, новый CostSummarySearchIndex.cs 44, CostSummarySearchIndexTests.cs 62 строк. Поиск выделен в Core-компонент, подготовка на worker; декомпозиция остальной Сводной остаётся отдельной задачей. Сборка/тесты не запускались.

- 26.11.2026, PERF-026 / ARCH-SIZE: EstimateView.CostSummary.cs 1350→1355, новый SummaryRowSequence.cs 51, Program.cs (App.Checks) 635→636, новый SummaryRowSequenceChecks.cs 60 строк. Вынесено сравнение строк/восстановление ключей в самостоятельный компонент и отдельный тест. Рост крупных файлов — только точки интеграции; общая декомпозиция остаётся отдельной задачей. Сборка/тесты не запускались.

- 26.11.2026, PERF-025 / ARCH-SIZE: SqlServerCostSummary.cs 809→674, новый SqlServerCostSummary.Read.cs 164, PilotSchema.cs 168→176, PilotCases.cs 487→489, PilotExtendedCases.cs 431→433, новый PilotCostSummaryCases.cs 78 строк. Read-проекция выделена из SqlServerCostSummary; тестовые fixture/check вынесены отдельно. Изменения synthetic schema не являются production migration. Сборка/тесты не запускались.

- 26.11.2026, PERF-025 / ARCH-SIZE: SqlServerCostSummary.Read.cs 164→172, SqlServerCostTotals.cs 218→234, CostSummaryPane.xaml.cs 487→486, EstimateView.CostTotals.cs 291→306, EstimateView.CostSummary.cs 1355→1358, EstimateView.Header.cs 458→466, PilotCostSummaryCases.cs 78→95, новый CostFinancialSnapshot.cs 34 строк. Новая проверка SQL snapshot выделена в самостоятельный Core-компонент; рост существующих файлов — интеграция и guard записи. Сборка/тесты не запускались.
- 26.11.2026, PERF-007 / ARCH-SIZE-04/07: MainWindow.xaml.cs 1933→1926, Workspace.cs 245→284, PilotCases.cs 489→490, новый PilotWorkspaceNavigationCases.cs 48 строк. SQL read/mapping остаются в Core, UI использует один маршрут и уменьшен; общая декомпозиция MainWindow остаётся отдельной задачей. Сборка/тесты не запускались.
- 26.11.2026, PERF-007 / ARCH-SIZE-07: Estimate.cs 512→521, новый EstimateReader.Cards.cs 110, Workspace.cs 284→293, PilotCases.cs 490→491, новый PilotObjectCardCases.cs 55 строк. Обособлен read-only DTO/reader карточек, общий финансовый расчёт вынесен в helper, полный UI-документ больше не удерживается карточкой. Partial — организационное разделение SQL-проекций, общая декомпозиция остаётся отдельной задачей. Сборка/тесты не запускались.
- 26.11.2026, PERF-007 / ARCH-SIZE-04: MainWindow.xaml.cs 1926→1878, MainWindow.Navigation.cs 218→341, DirectorView.xaml.cs 239→241, Workspace.cs 293→307, PilotWorkspaceNavigationCases.cs 48→52, новый WorkspaceRefreshSourceTests.cs 49 строк. Три существующих метода навигации перенесены в тематический partial, новый SQL snapshot guard в Core; общий logical MainWindow не декомпозирован, Windows build/test не запускались.
- 26.11.2026, PERF-007 / ARCH-SIZE-07: Totals.cs 334→340, новый Totals.SqlSums.cs 66, PilotCases.cs 491→492, новый PilotStreamedTotalsCases.cs 67 строк. Отдельная SQL-проекция накопителей, общая формула FinishContract; partial не означает полной декомпозиции. Сборка/тесты не запускались.
- 26.11.2026, PERF-007 / ARCH-SIZE-07: TagRules.cs 65→84, EstimateReader.Cards.cs 110→118, Finance.cs 278→299, Workspace.cs 307→312, PilotObjectCardCases.cs 55→74 строк. Общий снимок правил и реквизитов передаётся между двумя read-only projections; полная декомпозиция не выполнена, сборка/тесты не запускались.
- 26.11.2026, PERF-035/037 / ARCH-SIZE-07: Finance.cs 299→367, PilotCases.cs 492→493, новый PilotFinanceBatchCases.cs 83 строк. Разделены SQL templates и общие row parsers, read-only batch не меняет денежные формулы; полная декомпозиция не выполнена, build/test не запускались.
- 26.11.2026, PERF-035/037 / ARCH-SIZE-07: Finance.cs 367→295, новый Finance.Acts.cs 125, PilotFinanceBatchCases.cs 83→132 строк. В тематический partial выделено чтение и расчёт КС, logical FinanceReader не декомпозирован; сборка/тесты не запускались.
- 26.11.2026, PERF-036/035 / ARCH-SIZE-07: SqlServerDirector.cs 221→228, PilotCases.cs 493→494, новый PilotDirectorMovementCases.cs 87 строк. Раздельная SQL projection по необходимости просрочек; общий код сохранён, build/test не запускались.
- 26.11.2026, PERF-041 / ARCH-SIZE-03/07: Estimate.cs 521→506, новый EstimateReader.Totals.cs 25, Totals.cs 340→374, EstimateDisplayTests.cs 71→76, новый EstimateTotalsProjectionTests.cs 66 строк. Вынесен тематический расчёт, generic reuse исходных строк; logical reader не полностью декомпозирован, build/test не запускались.
- 26.11.2026, PERF-041 / ARCH-SIZE-03: EditableEstimateRow.cs 271→313, Program.cs (App.Checks) 636→637, новый EstimateRowReadbackChecks.cs 79 строк. Изменения локальны в модели, новый сценарий выделен из runner; build/test не запускались.

- 26.11.2026, PERF-041 / ARCH-SIZE-03: EstimateView.xaml.cs 1158→1163, новый EstimateGridRowUpdates.cs 70, Program.cs (App.Checks) 637→638, новый EstimateGridRowUpdateChecks.cs 60 строк. Изолирован компонент публикации строк, интеграция в крупный класс минимальна; build/test не запускались.

- 26.11.2026, PERF-041 / ARCH-SIZE-03: EstimateView.xaml.cs 1163→1155, EstimateRowViewIndex.cs 45→112, Program.cs (App.Checks) 638→639, новый EstimateRowIndexReadbackChecks.cs 70 строк. Логика согласования моделей/индексов вынесена из крупного view в существующий индекс; build/test не запускались.

- 26.11.2026, PERF-018 / ARCH-SIZE-03: EstimateView.xaml 2308→2307, EstimateView.xaml.cs 1155→1165, Program.cs (App.Checks) 669→672, новый LazyContractActsChecks.cs 38 строк. КС уже самостоятельный UserControl, отложено его создание; рост code-behind ограничен фабрикой и guards. Сборка/тесты не запускались.

- 26.11.2026, PERF-018 / ARCH-SIZE-03: EstimateView.xaml 2307→2199, EstimateView.xaml.cs 1165→1165, EstimateView.CostTotals.cs 306→325, новый EstimateTotalsPanel.xaml 108 / .xaml.cs 9, Program.cs (App.Checks) 672→675, новый LazyEstimateTotalsChecks.cs 36 строк. Разметка отдельного функционального раздела вынесена из монолита; финансовая логика и SQL остаются прежними. Сборка/тесты не запускались.

- 26.11.2026, PERF-018 / ARCH-SIZE-03/05: EstimateView.xaml 2197→1863, EstimateView.Materials.cs 1718→1720, EstimateView.Groups.cs 196→197, новый EstimateMaterialsPanel.xaml 348 / .xaml.cs 9, EstimateView.MaterialsPanel.cs 58, Program.cs (App.Checks) 675→678, новый LazyEstimateMaterialsChecks.cs 41, NameWrapPreferenceSourceTests.cs 63→64 строк. Разметка и wiring выделены, доменная логика материалов ещё требует декомпозиции. Параллельная настройка переноса наименований сохранена. Сборка/тесты не запускались.

- 26.11.2026, PERF-018 / ARCH-SIZE-03: EstimateView.xaml 1863→1027, EstimateSummaryPanel.xaml 841, EstimateView.CostSummary.cs 1358→1365, EstimateSummaryPanel.xaml.cs 29, EstimateView.SummaryPanel.cs 39, SummaryViewSourceTests.cs 86→115, Program.cs 678→681, LazyEstimateSummaryChecks.cs 38 строк. Разметка Сводной и её event forwarding выделены; финансовая логика и SQL остаются прежними. Сборка/тесты не запускались. Новый отдельный XAML больше целевых 500 строк из-за сохранения связной таблицы и шаблонов; дальнейшая декомпозиция только по ответственности после WPF-приёмки.

- 26.11.2026, PERF-046 / ARCH-SIZE-03: EstimateView.CostSummary.cs 1365→1363, SummaryColumnFit.cs 168→182, SummaryColumnFitChecks.cs 54→67, SummaryViewSourceTests.cs 115→132 строк. Из крупного code-behind удалено дублирование snapshot; проверка идентичности и cache reuse остаются в самостоятельном SummaryColumnFit. Сборка/тесты не запускались.

- 26.11.2026, PERF-046/012: EstimateView.CostSummary.cs 1363→1390, EstimateView.xaml.cs 1155→1159, SqlServerCostSummary.Read.cs 172→190, SqlReadCancellation.cs 26, Totals.cs 374→381, Totals.SqlSums.cs 66→68, CostSummaryBatchValidationTests.cs 35→44, PilotCostSummaryCases.cs 95→110 строк. Новый SqlReadCancellation изолирует provider bridge; рост code-behind обусловлен lifecycle guards, бизнес-расчёты и SQL сохранены. Сборка/тесты не запускались.

- 26.11.2026, PERF-046/012: EstimateView.Materials.cs 1720→1748, EstimateView.xaml.cs 1159→1165, SqlServerCostSummary.cs 674→574, SqlServerCostSummary.Materials.cs 123, CostSummaryBatchValidationTests.cs 44→53, PilotCostSummaryCases.cs 110→127 строк. DTO и SQL-reader материалов выделены по ответственности; lifecycle guards остаются рядом с загрузкой в EstimateView. Формулы/SQL сохранены. Сборка/тесты не запускались.

- 26.11.2026, PERF-046/012: EstimateView.CostTotals.cs 325→356, EstimateView.xaml.cs 1165→1170, CostFinancialSnapshot.cs 34→69, SqlServerCostTotals.cs 234→249, CostSummaryBatchValidationTests.cs 53→67, PilotCostSummaryCases.cs 127→150 строк. Общий revision fence/projection в CostFinancialSnapshot, lifecycle guards рядом с загрузкой Итогов; рост крупного EstimateView.xaml.cs ограничен маршрутизацией detach/reattach. SQL/формулы сохранены. Сборка/тесты не запускались.

- 26.11.2026, PERF-046/012: ContractActsWorkspace.xaml.cs 645→678, SqlServerActs.Grid.cs 451→222, SqlServerActs.ProgressSummary.cs 274, SqlServerActs.cs 1257→1152, SqlServerActs.ActList.cs 130, Totals.cs 381→389, KsActsParityTests.cs 103→116, PilotFinanceBatchCases.cs 132→159 строк. Чтение списка актов и сводной по выполнению выделено в SqlServerActs.ActList/ProgressSummary; старые финансовые формулы и транзакционные вызовы сохранены. Сборка/тесты не запускались.

- 26.11.2026, PERF-045/GRID-21: App.xaml 1559→1565, EstimateView.xaml 1027→1030, EstimateScrollPolicy.cs 18, EstimateView.ColumnLayout.cs 207→207, Program.cs 681→682, EstimateScrollingChecks.cs 82 строк. Политика большого грида вынесена в небольшой converter, шаблон scrollbar изменён на месте (6 строк, не отдельный рефакторинг темы). Build/test не запускались.

- 26.11.2026, PERF-045: EstimateView.xaml 1030→1029, EstimateScrollingChecks.cs 82→78 строк; удалён EstimateScrollPolicy.cs (18 строк), вместо условной политики один штатный XAML property. Build/test не запускались.

- 26.11.2026, PERF-041/046: SqlServerActs.Grid.cs 222→153, KsActSheetProjection.cs 84, ActsEditorView.xaml.cs 2345→2348, KsActSheetProjectionTests.cs 99 строк. NumberRows выделен как pure компонент, крупный ActsEditorView изменён точечно, без новых обязанностей. Build/test не запускались.
