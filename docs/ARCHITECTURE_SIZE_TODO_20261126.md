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
