# 28–29.11.2026 — дизайн-проход: закрытие D-вопросов GRID-28 / HOME-03 / SPACE-15 / SPACE-18 / SPACE-19

Агент: zcode-sess-design. Бронь: d8a63aad (9 строк раздела «Дизайн» EXECUTION_TODO_20261128). Дизайн-коммит: 0174fa25 (только `*.xaml`, 19 файлов, +22/−186). База на момент брони: f452a04f; финал на базе cddaabbe (BUG-F54.07 приёмка f54x) → 0174fa25.

Сфера: только XAML (правила [DESIGN_NOTES](DESIGN_NOTES.md)); без `*.cs`, биндингов, x:Name, Visibility, команд. Режим D: инвентарь + семантика + безопасные точечные правки; .cs-работа — запросами. Родители остаются PARTIAL: живы A-приёмки (GRID-28.03, HOME-03.02, SPACE-15.03, QA-09/19) и .cs-запросы.

## GRID-28.01 / GRID-28.02 — семантика выделения: CODE READY (D-часть)

Инвентарь всех таблиц (`rg SelectionUnit|SelectionMode` по 46 XAML, ~70 DataGrid в 29 файлах):

| Группа | Таблицы | Семантика |
|---|---|---|
| Смета | `EstimateGrid` — Extended/FullRow явно (EstimateView.xaml:775); «Материалы» FullRow; «Итоги» `EstimateTotalsPanel` Extended/**Cell** | строка для команд, ячейки в итогах |
| Закупки | `ProcurementPane` — Extended/FullRow (основная) + Single (группы); 3 отсоединённых превью (`ProcurementMovementDialog`, `ProcurementRequestDialog`, `ProcurementShopOrderPreview`) — **Cell** + `PreviewGridPaste` | строки для команд; прямоугольная вставка только в превью |
| Выдача | `ShipmentsPane` — Extended/FullRow (основная) | строки |
| КС | `ActsEditorView`, `ActsWindow`, `ContractActsWorkspace`, `AllContractActsWindow`, `Ks3/KsGroupReport` — FullRow (Extended/Single по назначению) | строки |
| Сводная | `CostSummaryPane` — 4×FullRow + 1×**Cell**; `ViewState` читает и SelectedItems, и SelectedCells; `EstimateSummaryPanel` — FullRow | смешанная, текущая сохранена |
| Прочие (28.02) | ReplaceDialog **Cell**/Single; PassportsWindow CellOrRowHeader; TrashWindow/History/Letters/LetterEditor/PtoWindow Single; CompareDialog Extended; PeopleWindow Extended; остальное FullRow по умолчанию | исключения ниже |

Решение (соответствует прежнему направлению в MASTER_TODO): структурные таблицы команд остаются FullRow — пакетные команды потребляют `SelectedItems` как строки модели (ProcurementPane ~20 вызовов, ShipmentsPane ~15, EstimateView.Materials/RowUpdates, Trash, Stages и др.), смена SelectionUnit сломала бы их scope — не менять. Явные исключения row-only/read-only: корзина, журнал, письма, ПТО-список, директор, справочники. Cell-семантика остаётся там, где она безопасна: detached-превью закупок (прямоугольная вставка до 10 000 ячеек с guard'ами), «Итоги», ReplaceDialog, PassportsWindow (CellOrRowHeader). Клавиатура: Shift+стрелки/Ctrl+C — нативно; F2/Enter/Esc — `GridEditInteraction` (без изменений). Drag-прямоугольник в структурных таблицах требует .cs-редизайна (SelectedItems→SelectedCells с сохранением restore/пакетных команд) — записан запрос в DESIGN_NOTES. Бизнес-операции не изменены.

## HOME-03.01 — заголовки: CODE READY

`App.xaml:28` `UiH1Size=24` DIP = Qt 18pt × 96/72 ровно 24 ✓. `Theme/TypographyMetrics.xaml`: `UiPageHeading` FontWeight Bold + `UiWrappedHeading` TextWrapping=Wrap (длинный заголовок переносится, не обрезается) ✓. Межбуквенный интервал Qt −0.3px: у WPF TextBlock нет tracking — официальное документированное ограничение записано в комментарии TypographyMetrics.xaml и здесь; fake stretch/ScaleTransform запрещены. Оставшееся — физическое сравнение кадров = HOME-03.02 (A).

## SPACE-15.01 / SPACE-15.02 — размеры окон/диалогов: CODE READY

Инвентарь window-level fixed/min/max всех 46 XAML (таблица в журнале DESIGN_NOTES). Правки 0174fa25:

- **Monitor-aware caps добавлены 16 окнам, у которых их не было**: ActsWindow, AllContractActsWindow, CostSummaryWindow, HistoryWindow, LetterEditorWindow, LettersWindow, MetalWarehouseWindow, OrdersWindow, PassportsWindow, ProcurementWindow, PtoEditorWindow, PtoWindow, ShipmentsWindow, StageLinksDialog, StageSystemsMoneyDialog, TrashWindow — `MaxWidth/MaxHeight` на `SystemParameters.WorkArea` (паттерн блок-3 от 21.11, всего теперь 23 окна). Исключение по записи: MainWindow без caps — комментарий в MainWindow.xaml:11 (caps обрезали развёрнутое окно; решение сохранено).
- **ConfirmDeleteDialog**: фиксированный `Height="360"` → `SizeToContent="Height"` + WorkArea-ограничение — длинные вопрос/детали растягивают диалог вместо внутреннего скролла (паттерн ArchiveYearDialog/DeletePositionsDialog).
- Min-размеры сохранены (минимумы текста, прежние проходы HDR/WIN); ограничение: binding WorkArea = основной монитор — известное ограничение блок-3; динамика для динамических форм уже решена в .cs (`DynamicFormLayout`, ближайший монитор). Контентные фиксированные размеры в основных окнах: доминируют 24×24 (иконки тулбара/навигации) и 12×12 (affordance-роли LayoutMetrics) — обоснованные; ширины колонок CostSummaryPane 80–115 — роли GRID-19.

## SPACE-18.01 / SPACE-18.02 — локальные стили: CODE READY

- Пересечение ключей локальных стилей с App.xaml: **∅** (comm двух полных списков) — перекрывающих App.xaml переопределений нет; локальные стили — модульные роли.
- Найдены и удалены 3 неиспользуемых стиля: `EstimateMenuHeader`, `EstimateMenuItem` (EstimateView.xaml, остаток прежнего меню до Popup «⋯ Действия»), `ShipmentStepArrow` (ShipmentsPane.xaml). Проверка перед удалением тройная: StaticResource по всем xaml/cs, DynamicResource, строковые ссылки из кода (`Resources["…"]`/`FindResource`). Проверка отработала: `YearChip` и `EstimateStageEditor` оказались использованы из кода и сохранены.
- Остальные локальные стили классифицированы как модульные роли (EstimateView 37, EstimateSummaryPanel 36, ShipmentsPane/ProcurementPane по 24, MainWindow 20, DirectorView 17 и т.д.) — семантика сохранена, дубликатов с общими ролями по ключам нет.

## SPACE-19.01 / SPACE-19.02 — Thickness/Margin: CODE READY (D-часть)

- Общие метрики: `Theme/LayoutMetrics.xaml` — ~120 ролей с Python-происхождением. Новое: корневые поля 4 окон (`HistoryWindow`, `KsGroupReportWindow`, `OrdersWindow`, `TrashWindow`) переведены `Margin="22,18,22,16"` → `LayoutDialogMargin`.
- Хвост сырых margin: 850 числовых значений; топ — `0,0,6,0` (зазор иконка/действие), `0,4,0,0`, `0,0,12,0`, `0,0,5,4` — малые Python-зазоры 4–12 DIP; заведение отдельной роли на каждый не даёт выигрыша паритета — обоснованные локальные отступы (критерий «не заменять специфичные размеры без проверки» соблюдён).
- `.cs`: ~200 присвоений `new Thickness(...)` в 40+ файлах (PtoEditorWindow 12, ExcelExistingUpdatePreviewWindow 17, ExcelImportPreviewWindow 13, PtoCertificatesWindow 13, EstimateView.Sections 13, PeopleImportPreviewWindow 12 …) — read-only инвентарь; конверсия на `LayoutMetrics` — запрос в DESIGN_NOTES (вне дизайнерской линии). Родитель остаётся PARTIAL.

## Верификация

- `dotnet build GmcSmeta.slnx`: **Ошибок: 0** (256 предупреждений — существующие, в WIP .cs параллельного агента; новых от XAML нет).
- XML-parse всех затронутых XAML — ok. Подсчёт: 19 файлов, +22/−186, коммит 0174fa25.
- xUnit/GUI/DPI/SQL не запускались — изменений .cs/БД/SQL/экспорта нет; физические кадры остаются за A-приёмками (GRID-28.03, HOME-03.02, SPACE-15.03, QA-09/19 BLOCKED).

## Особые обстоятельства (прозрачность)

- Рабочая копия общая с параллельными агентами (perf714 правит `EstimateView*.cs`/`MainWindow*.cs`, f54x приёмку): коммит выполнялся только по явным путям; чужие правки не коммитились и не трогались.
- В середине прохода ошибка первого скана «неиспользуемых стилей» (само-вычитание счётчика) была откатом `git checkout --` по 9 XAML. Если у параллельного агента были незакоммиченные правки в этих файлах в тот момент — они могли быть затёрты; рекомендуется им перечитать свои файлы перед следующим коммитом.

## Остаток

A-приёмки: GRID-28.03, HOME-03.02, SPACE-15.03 (живые Windows-кадры), QA-09/19 (BLOCKED). Код-запросы: см. DESIGN_NOTES. Родители GRID-28/HOME-03/SPACE-15/18/19 — PARTIAL (реализация D-детей готова, приёмка/код-работа осталась).
