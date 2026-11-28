# 28.11.2026 — проход по всем 14 оставшимся визуальным ID

Исходный остаток: 239 CODE READY / 253, 14 PARTIAL. База 8d03c5dccc62c2a43f75b8a9c767e793d43a04a5, затем параллельные функциональные изменения; чужие изменения не отменялись. Включено также измерение ширин из предшествующего короткого прохода fe1e065/743f2de.

| ID | Результат прохода | Остаток |
|---|---|---|
| GRID-19 | SummaryColumnFit учитывает состав/заголовки колонок, family/style/stretch/size, DPI, pixels-per-DIP; при изменении effective metrics запрашивает coalesced fit даже без resize; добавлены проверки. В legacy Totals убран фиксированный header height. | PARTIAL: остальные таблицы и реальная геометрия длинных денежных полей. |
| GRID-28 | Обе поверхности итогов теперь Extended Cell / ExcludeHeader, без изменения обработчиков записи; матрица содержит drag/Shift/edit-to-selection. Проверены основные FullRow grids, их SelectedItems, guards и batch operations. | PARTIAL: нельзя глобально менять FullRow на Cell без адаптации выбора реальных строк, пакетных удалений/перемещений, контекстных команд, drag/drop, восстановления выбора и clipboard. Ctrl+V диапазоном для финансовых итогов не добавлен. |
| TOT-11 | EstimateView.Header.cs: silent restore через try/finally, неизвестный tag очищает выбор, нет ложной записи. Существующие mode/locked/stage/revision guards сохранены. PilotStreamedTotalsCases расширен четырьмя режимами, transport-edit readback, крупной суммой, отклонением чужого режима и stage-derived sum; прежний high-precision percent guard сохранён. | CODE READY: фактический Pilot и UI acceptance pending. |
| HOME-03 | Python main theme.py подтверждает final H1 18pt/700/−0.3px; оба shell headings используют UiPageHeading и UiH1Size, естественный wrap. | PARTIAL: WPF TextBlock не имеет штатного tracking, не имитируем сжатием текста; точное соответствие и DPI-приёмка не подтверждены. |
| SPACE-11 | Legacy totals table переведена с PythonTile на SummaryTableFrame, единственная рамка, clipping и осмысленный нижний inset; gap принадлежит карточкам. Предыдущий audit 46 XAML: прямых Border/Border нет. | PARTIAL: непрямые вложения и dynamic template hosts ещё требуют полной ревизии. |
| SPACE-15 | ProcurementExtraDialog адаптивные две колонки, MinWidth 340 вместо 430, CanResize, прокрутка с отдельным footer; PTO label width теперь cap, а не fixed; три wrap footers. | PARTIAL: другие фиксированные размеры и вторичный монитор. |
| SPACE-18 | Общие стили и метрики полей/кнопок в ProcurementExtra, SectionPicker, MaterialsMarkup, PtoFormUi; отдельное поле многострочного имени сохраняет обоснованный min54. | PARTIAL: прочие локальные overrides. |
| SPACE-19 | Те же четыре dynamic UI surfaces переведены на LayoutFormFieldMargin/ColumnGap/StackedFieldGap/DialogActionGap/IntroGap; локальные расчётные размеры/отступы не удаляются вслепую. | PARTIAL: полная инвентаризация всех dynamic UI. |
| FONT-08 | Сверены Python final theme H1/H2, tile, nav/header and segment selected 600. Segment checked Bold заменён на SemiBold; H1 700 сохранён. Исправлен stale source guard для вынесенного дерева и totals font resource. | PARTIAL: полная таблица соответствия систем/итогов и прочих локальных weights. |
| FONT-13 | Проверены 42 icon templates (34 IconLabel, 8 IconOnly), center у всех 34 подписей и viewboxes, menu and navigation presenters centered. Предыдущая проверка 46 XAML: нет отрицательных literal Margin у надписей; intentional tab seam retained. PTO label top→center; расширен compiled WPF fixture для 34 icon labels × 3 размера текста с допуском 1 DIP; source guards. | CODE READY: реальные Windows/DPI и визуальный baseline pending. |
| QA-08 | В текущих деревьях обоих репозиториев нет исторических _tools/audit_snap.ps1 и audit_parity.ps1. Добавлены новые явно обозначенные replacement entry points tools/local-qa/audit_snap.ps1 и audit_parity.ps1, вызывающие существующий physical capture/pair, проверка exit code, create-new evidence. | PARTIAL: replacement вместо оригиналов, не запускался и не подтверждён владельцем. |
| QA-09 | physical matrix states разделены для КП/договора, KS2/KS3, ПТО, metal, catalog; тесты coverage добавлены. | PARTIAL: фактические paired Python/CSharp captures отсутствуют. |
| QA-19 | Offline MainWindow fixture теперь 3 обычных детей + remainder + deep child, проверяет их принадлежность. В physical matrix явно logo, three-stage-children, selected-deep-child, collapsed-parent. | PARTIAL: нужны реальные увеличенные физические captures и review. |
| SUM-VIS-01 | SummaryColumnFit исправлен, включая новый запрос при effective font/DPI и проверку новых/удалённых columns; legacy totals frame, gap, header и selection улучшены. | PARTIAL: C12, реальная anchor/scroll behavior, legacy route и E01–E06. |

## QA commands
В интерактивной Windows-сессии на согласованной синтетической GmcSmetaPilot_*:
```powershell
python tools/local-qa/visual_capture.py matrix --out visual-matrix.json
# Пример параметров, подставить реальный PID и проверенный fixture JSON:
.\tools\local-qa\audit_snap.ps1 -TargetPid <PID> -Client Python -Fixture python-fixture.json -Resolution 1920x1080 -Dpi 120 -Out evidence-python
.\tools\local-qa\audit_snap.ps1 -TargetPid <PID> -Client CSharp -Fixture csharp-fixture.json -Resolution 1920x1080 -Dpi 120 -Out evidence-csharp
.\tools\local-qa\audit_parity.ps1 -PythonCapture evidence-python/capture.json -CSharpCapture evidence-csharp/capture.json -Out pair.json
```
Capture checks real foreground PID, monitor DPI and resolution, geometry, fixture SHA and synthetic confirmation. Pair validates metadata and PNG hash; never asserts visual acceptance. No fake PNGs. Need manual side-by-side review.

## Проверка и границы
- Прочитаны исходники и изменения из main; проверена лексическая сбалансированность 5 изменённых XAML; проверены ключи ресурсов и явные selection settings. Добавлены source cases, offline WPF fixtures, Pilot scenarios and Python matrix tests. Ничего из .NET/Windows/Pilot/Python тестов не запускалось; компиляция не подтверждена.
- Выбор без массового paste в финансовых итогах, новый minimum width, native H1 shaping and wrapper replacement are implementation decisions. Суммы, формулы, SQL production logic, схема, реальные БД, release unchanged; Pilot modifications are test source only.
- CODE READY +2: TOT-11, FONT-13; 241/253, 12 PARTIAL. Не закрывать исходные checkbox до DONE.
