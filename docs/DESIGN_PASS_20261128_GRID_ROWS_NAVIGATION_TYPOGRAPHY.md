# 28.11.2026 — grid rows, production navigation and dynamic typography

Baseline f213a27ae1b5dc4552d6f3a2d785bd9daab2ea87. Three canonical design IDs advance to CODE READY (implementation/check code, not witnessed acceptance).

## SPACE-14 — empty Grid.RowDefinitions
- Read all 46 app XAML files from main, inventoried 211 declared rows and direct child row/spans, no out-of-range references. Three statically unoccupied rows are intentional: EmptyPage 2*/3* elastic whitespace positioning the empty prompt; ProcurementFilterLayout Auto second row populated by the width-dependent handler. Added explanations in XAML; no change to these layouts.
- Reviewed 11 indexed C# dynamic row construction files (ObjectTotalsLayout, ProcurementShopOrderPreview, ProcurementMovementDialog, ExcelImportPreviewWindow, KsActPropertiesDialog, MetalDialogs, WorkspaceQueryWindows, ProcurementOrderDialog, EstimateView.SpecUpdate, ProcurementRequestDialog, WorkspaceReferenceWindows). Deliberate splitter rows have children, dynamic forms allocate each row with its fields.
- MetalFormDialog now uses shared form labels/field metrics and work-area bounds, no action row in scrollable grid; its three derived forms have separate pinned wrapping confirm/cancel footer. KsActPropertiesDialog similarly uses common actions and named fields. Existing handlers and return values retained.
- GridRowCoverageTests checks all XAML row assignments/spans and exact three exceptions, with two dynamic form guards. Tests authored, not executed. The static audit is lexical, not a WPF compiler.

## NAV-20 — all real navigation roles
- Replaced hand-built mock tree fixture with actual MainWindow.RefreshNavigation and lazy expansion using synthetic ObjectRow/DocRow data, all three production modes (contracts, proposals, archive), year, object/folder, KP, agreement/addendum, stage parent, nested stage and remainder, empty folder in KP mode.
- Check normal/selected marker, expansion/collapse, real header bounds at 270/300/420 DIP, deep hierarchy and remainder italics, long name and nonstandard date. The fixture keeps _ready false and does not initialize SQL; it uses reflection for existing private data/projection rather than shipping a new fixture API in production. Selection does not open a document. Corrected stale source assertions after concurrent navigation code extraction.
- Physical Windows/DPI rendering and paired screenshots remain acceptance (QA-09/19 not promoted).

## FONT-12 — dynamic window font roles
- Reviewed 17 indexed C# UI files containing FontSize and seven indexed workbook/export files. No direct numeric UI font sizes or numeric FontSizeProperty setter found. Workbook font sizes intentionally excluded; measured grid zoom retained.
- Dynamic stage card amount/status use live named font resources, wrap long name/amount/status and keep full tooltip; named spacing resource added. PTO import, certificate library and metal register use shared chrome/work-area helper; duplicate font initialization removed. PTO import field labels use semantic text, fields have shared height and accessibility names; certificate search grows from minimum rather than fixed height.
- DynamicTypographySourceTests guards the entire app source tree for direct numeric font size assignments/setters excluding workbook .Font.FontSize, checks five dynamic form chrome surfaces and the stage card roles. This is a conservative source regression, not a compiler/dataflow proof. GitHub indexed source review may lag; full local test run required.

## Other work and acceptance
SPACE-15/18/19, FONT-10/13 and GRID-19 receive partial evidence; no other promotion. QA-08 historical scripts absent from current trees; older commit tree requests failed; no replacement falsely described as recovered. HOME-03 letter spacing and actual screenshot tasks remain open.

Design decisions: retain intentional empty-state positioning and responsive filter row; shared form bounds use existing primary work-area policy, not new per-monitor placement. Enter/Esc now standard confirm/cancel in metal and KS property dialogs; confirmation callbacks unchanged. Stage cards may grow vertically for long captions instead of truncation. No SQL, schema, calculations, exports, production data, version or release changes. No Windows build/xUnit/App.Checks, physical input or paired screenshots executed. Three canonical IDs advance, 238/253 CODE READY and 15 PARTIAL. Next acceptance: Release build, full Core test suite, compiled WPF checks, then same-data Windows 100/125/150% DPI. Test only synthetic or GmcSmetaPilot_* fixtures.
