# 28.11.2026 — проход по 12 оставшимся визуальным задачам

Начало: 241/253 CODE READY, 12 PARTIAL. Изменения напрямую в main; учитывались параллельные функциональные коммиты. Реальные базы данных, схема, финансовые формулы, версия и релиз не менялись.

| ID | Результат | Остаток |
|---|---|---|
| GRID-19 | SummaryColumnFit observes header/key/column identity changes even without a new viewport size; shapes nonnumeric text and header words with FormattedText, retains bounded glyph cache for numeric values and manual widths. | PARTIAL: all other grids and real DPI/long-value geometry. |
| GRID-28 | Detached supplier request, movement and workshop preview tables now use Extended Cell, native copy, guarded rectangular Ctrl+V. PreviewGridPaste validates complete rectangular TSV (including quoted tabs/newlines and escaped quotes), target bounds, visible/reordered columns, writable allowlist and numeric conversion before setting detached DTO properties. Invalid last cell or read-only target refuses entire range; preserve affected row identities after sorted refresh. Active editor retains native paste. Existing Accept/Make commits edit and processes all rows, not selected cells. Source and WPF tests authored. | PARTIAL: estimate, materials, KS and other structural row commands still FullRow; no global selection change or SQL range paste. |
| HOME-03 | Director HeadingText now uses same UiPageHeading/UiH1Size as the two shell headings. Native wrap and 24 DIP/700 preserved. | PARTIAL: exact Qt -0.3px tracking not implemented, deliberate native shaping until approved; physical review pending. |
| SPACE-11 | design_audit.py detects indirect nested frame padding candidates through panels, excludes independent popup/template trees, flags rather than blindly removes. Existing frame changes retained. | PARTIAL: review generated candidates and resolve style/template ownership on Windows. |
| SPACE-15 | Procurement order two flexible columns, 145 DIP label cap, natural row height, scroll body / wrapping pinned actions; movement 155 DIP label cap, date width cap, actions without fixed heights; request/workshop preview shared field/action heights. | PARTIAL: other fixed sizes and secondary monitor DPI. |
| SPACE-18 | ProcurementDialogChrome shared typography, caption and resource-based spacing; four dialogs use shared field/action roles; layout-review.csv explicitly separates non-resource candidate overrides. | PARTIAL: review all remaining local overrides. |
| SPACE-19 | ProcurementDialogChrome Outer/RequestOuter/SectionGap/FooterGap use actual existing LayoutMetrics keys instead of duplicate literal Thickness. Four dialogs use shared field, label, intro, footer/action gap resources. Scanner recognizes SetResourceReference as a resource reference. | PARTIAL: exhaustive dynamic C# review. |
| FONT-08 | Confirmed final Python weights in theme.py, kp_editor.py, cost_summary_view.py, cost_totals_view.py, director_view.py and main_window.py. Mapping below; unselected Segment now Normal rather than Medium, selected remains SemiBold; common page heading and row/total weights pinned by 16 source cases. | CODE READY (semantic roles); physical font fallback and local exceptions reviewed separately under SPACE-18/visual QA. |
| QA-08 | Existing historical scripts unavailable; maintained replacement wrappers now capture and compare real paired PNGs with optional bounded same-coordinate crop, verified PNG hashes, CRC, bounded decode and informational absolute pixel difference + changed area. Both originals' hashes and derived output hashes retained; no overwrite; optional -DiffOut/-Region. | CODE READY for replacement implementation, not recovery of original historical files. Actual PowerShell/Windows run and owner acceptance remain. |
| QA-09 | Full physical matrix from preceding pass preserved; compare_capture_pair generates original pair comparison evidence without asserting acceptance. | PARTIAL: actual Python/CSharp captures not present. |
| QA-19 | Crop comparison can produce enlarged inspection material from real paired captures with coordinates and verified source hashes; existing logo/stage matrix preserved. | PARTIAL: actual logo and complex-tree images not present. |
| SUM-VIS-01 | Summary header invalidation and shaped header/unit measurement; manual widths preserved; no change to row projection or finance. | PARTIAL: real anchor/scroll behavior and C12/legacy route/E01–E06. |

## Semantic font weight reference

| Qt source role | Qt numeric weight | WPF |
|---|---:|---|
| Body, ordinary rows, unselected segments / secondary tabs | 400 (Normal) | Normal |
| Optional medium emphasis, no explicit Python Weight.Medium found in indexed source | 500 | Medium only when a specific documented role requires it; removed accidental Segment default |
| H2, primary actions, selected segments/filters/tabs, headers, tile labels, navigation selected and sub-systems/temporary groups | 600 (DemiBold) | SemiBold |
| H1, AppTitle, final tile values, main systems, summary system/total rows, total/earned emphasis | 700 (Bold) | Bold |
| Summary extra row | 400 | Normal |
| Component/subposition text | Normal + italic where appropriate | Normal + Italic |
| Explicit procurement orange primary / review-only semantic emphasis | intentional module-specific | retain existing documented local role; audit SPACE-18 separately |

Final Python QSS H1 is 18pt/700/letter-spacing −0.3px; WPF 24 DIP, native text shaping, no fake stretch. Body 10pt, H2 12pt, final tile 15pt; summary explicit px 19/22 retained. Main system vs nested system and temporary grouping compared with kp_editor.py _load and _build_view. Summary and total rows compared with cost_summary_view.py and cost_totals_view.py. Scope FONT-08 is semantic roles, not proof of actual selected font face on every Windows machine.

## New QA usage

On interactive Windows, only matched synthetic Pilot data:
```powershell
.\tools\local-qa\audit_parity.ps1 -PythonCapture evidence-python/capture.json -CSharpCapture evidence-csharp/capture.json -Out pair.json -DiffOut pair-diff
# Optional identical pixel coordinates, choose from actual screenshots:
.\tools\local-qa\audit_parity.ps1 -PythonCapture evidence-python/capture.json -CSharpCapture evidence-csharp/capture.json -Out tree-pair.json -DiffOut tree-diff -Region 0,0,300,600
python tools/local-qa/design_audit.py scan --out design-inventory
```
The crop outputs python.png/csharp.png/difference.png/difference.json. Differences are informational and not visual acceptance. No fake screenshot generated. Inventory layout-review.csv is heuristic; candidates are not defects by default. Existing captures still require operator selection and review.

## Verification and decisions

Readback of 10 app files confirmed all referenced LayoutMetrics keys exist, two edited XAML tag stacks balanced, three preview installers present and no _grid.SelectedItems/_grid.SelectedCells dependencies in preview command paths. Added source checks, WPF range paste regression and Python crop/raster tests. Not compiled or executed: .NET build, xUnit, App.Checks, Python unittest, PowerShell audit and real Windows/Pilot acceptance. No actual DB operations. Intentional decisions: preview paste limited to detached DTOs, numeric range 0..1e12, max 10000 cells/1M chars; no formula evaluation or structural insert; full-range validation first. Historical QA-08 scripts replaced rather than silently claiming recovery; original compatibility not verified. Native H1 tracking discrepancy remains. No release.

CODE READY +2 (FONT-08 and QA-08 replacement implementation): 243/253, 10 PARTIAL. Original checkbox DONE only after actual acceptance.
