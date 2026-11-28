# Визуальная диагностика WPF (без SQL)

## Запуск на Windows

Из корня репозитория:

```powershell
dotnet run --project tests/GmcSmeta.App.Checks -- --visual-audit "$env:TEMP\gmc-visual-audit"
```

Команда использует настоящие ресурсы App.xaml и скомпилированный EstimateTotalsPanel,
но только искусственные строки и суммы. Не открывает SQL, настройки пользователя или документы.
Повторный запуск перезаписывает одноимённые отчёты в указанной папке.

- totals-1366x768 / 1920x1080 / 2560x1440: тестовая вкладка «Итоги» при заданных DIP-размерах.
- totals-narrow-640x320: узкая область; в TSV проверить высоту таблицы и прокрутку.
- icons-raster-96/120/144dpi: каталог всех IconLabel/IconOnly ресурсов, обычные,
  недоступные и активные (реальный checked ToggleButton) состояния.
- Каждый PNG сопровождается TSV с путём в Visual Tree, типом, именем, видимостью,
  координатами, Actual/DesiredSize, Margin/Padding, шрифтом и фактическим DPI.
  Вложенность пути позволяет восстановить цепочку отступов; текст ячеек в TSV не пишется.

Обычный --offline-check дополнительно проверяет фактические LayoutSlot двух групп
ResponsiveToolbarPanel: широкий → узкий → широкий.

## Границы проверки

PNG создаётся RenderTargetBitmap, а не захватом рабочего стола. Raster DPI 96/120/144
не меняет фактический мониторный DPI; проверка при 100/125/150% Windows остаётся отдельной.
Размеры DIP не равны физическим пикселям при масштабировании. Снимки не доказывают
совпадения с Python. Каталог проверяет общие шаблоны, а не контекст каждой команды;
hover/pressed/focus нужно проверять интерактивно. Открытие реальных КП, КС, склада,
дерева, редактирование и проверка 2500+ строк остаются отдельными сценариями
на согласованной тестовой копии GmcSmetaPilot_*. Рабочую БД не подключать.

Для сравнения с Python фиксировать SHA, размер окна, DPI, масштаб приложения/таблицы,
одинаковые права и настройки колонок, одинаковый документ и состояние вкладок.
Сохранять снимки двух программ с одинаковой геометрией и отмечать расхождения
в MASTER_TODO, не закрывать DONE до реальной приёмки.

## Статический аудит исходников и протокол парных снимков

Из корня C#:

```powershell
python tools/local-qa/design_audit.py self-test
python tools/local-qa/design_audit.py scan --out "$env:TEMP\gmc-design-inventory"
python tools/local-qa/design_audit.py ancestry "$env:TEMP\gmc-visual-audit\totals-1920x1080.tsv" "0/1"
python tools/local-qa/design_audit.py pair before.json after.json
```

design-inventory.csv содержит файл, строку открывающего XAML-элемента, атрибут,
значение, путь вложенности и классификацию REVIEW/RESOURCE/SHARED_CANVAS.
Числовые значения, локальные стили и implicit/default не являются дефектами
автоматически. Роли 12/14/17/18/20/28 допустимы в своём контексте; служебные
стрелки, числовые обозначения и осмысленные символы проверяются отдельно.
C#-сканер эвристический, не разрешает типы и стили и не анализирует вычисляемые
значения; могут быть ложные срабатывания и пропуски. XML parse-error фиксируется
отдельно. Папки bin/obj исключены. Общий visual_evidence.py также создаёт инвентарь.

TSV дополнен render bounds (DIP), marginSource/paddingSource из WPF
DependencyPropertyHelper, типом TemplatedParent. ancestry выбирает уникальный
путь или имя и выводит только предков выбранного элемента, без суммирования
соседних отступов. Используйте данные вместе с визуальным снимком.

Для pair передайте два JSON с одинаковыми database (только GmcSmetaPilot_*),
fixture_id, object_id, document_id, data_fingerprint (SHA-256 согласованного
набора данных), role, permissions, signed (bool), smr_mode, columns_profile,
page, state, app_scale, table_zoom, dpi_x, dpi_y, window_width_px,
window_height_px. В каждом также нужны sha и png соответствующей реализации.
Команда отклоняет несовпадающие или неполные записи; не вычисляет fingerprint
из базы и не подтверждает достоверность внесённых оператором данных.
METADATA_MATCH_NOT_VISUAL_ACCEPTANCE не является PASS или DONE.

## Разностные изображения с масками

`--visual-diff before.png after.png output [masks.json]` принимает одинаковые пиксельные размеры. Необязательный JSON — массив прямоугольников с X, Y, Width, Height в пикселях, внутри изображения. Маски исключаются из числителя и знаменателя; полностью замаскированный снимок отклоняется. `difference.png` показывает изменённые пиксели, `overlay.png` — немаскированный 50/50 overlay для ручной проверки, `difference.json` — координаты изменённой области и количество сравниваемых/замаскированных пикселей. Маски указываются вручную только для согласованных динамических сумм/курсора. Результат не является визуальной приёмкой.

## Владелец внешних отступов основных страниц

| Путь host → root | Владелец внешнего края | Внутренние исключения |
|---|---|---|
| MainWindow.PageHost → ObjectPage | LayoutPageMargin на ObjectPage | Карточки/легенды — собственные смысловые интервалы |
| PageHost → DirectorView | LayoutPageMargin на странице | Рамки и тени — не второй page margin |
| PageHost → EstimateView → EstimateTablePanel | Отступы header/toolbar/table/footer на секциях | Строки имеют только cell padding |
| EstimateView.MaterialsPanel → EstimateMaterialsPanel | LayoutEmbeddedPageMargin на host | Дочерний Grid без внешнего margin |
| EstimateView.SummaryPanel → EstimateSummaryPanel | LayoutSummaryPageMargin на host | Намеренно составной Python 24/6/24/14 + 12/8/20/16 |
| EstimateView.TotalsPanel → EstimateTotalsPanel | LayoutEmbeddedPageMargin на TotalsScroll | Статьи и плитки имеют отдельные локальные интервалы |
| ContractCostsPanel/ProcurementPanel/ShipmentsPanel → соответствующий pane | LayoutCostPaneMargin / LayoutProcurementPaneMargin / LayoutShipmentsPaneMargin на pane | Standalone Window.Resources переопределяет только свой ключ; CostTabs имеет вертикальный inset, горизонтальный 0 |
| ContractActsPanel → ContractActsWorkspace | LayoutEmbeddedPageMargin на дочернем Grid | Внутренние список актов и редактор |
| KsRequisitesWindow → Grid → ScrollViewer → RequisitesFields | LayoutDialogMargin на Grid; LayoutFormScrollInset только на fields | ScrollViewer.Padding=0, TextBox padding только внутри поля, кнопки вне scroll |

Не удалять намеренные смысловые отступы; физические координаты проверять по TSV ancestry. Таблица описывает основные страницы, вспомогательные локальные переопределения продолжают проверяться сканером.

### Шаблоны и содержимое ячеек

Segment/ObjectTab/ObjectSubTab/PythonTabAction/PythonFilterChip/PythonFilterRadio/PythonSecondaryTab: значение Padding задаёт стиль, единственный Border применяет TemplateBinding; Presenter не получает дополнительного Margin, сохраняет Horizontal/VerticalContentAlignment. Значения 6/0, 16/7, 14/5, 12/5, 14/7 сохранены. Отступы между вкладками — отдельный trailing gap, не padding текста. Общие текстовые роли CompactCostText, TotalsArticleText, ReportGridReviewText/Muted/Center/Right/StrongRight: Margin=0, Padding=0, cell owns inset; PythonGridEditor также нулевой. Поиск с 30/32 DIP резервом и ComboBox с отдельной стрелкой — намеренные исключения. SidebarSplitter: 5 DIP отдельной колонки, внутри линия 1 DIP, обе скрываются при Ctrl+B; строка возврата Auto, без наложения на PageHost. Это не заменяет проверку остальных локальных шаблонов на реальных страницах.
