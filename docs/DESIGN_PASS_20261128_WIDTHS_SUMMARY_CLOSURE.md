# 28.11.2026 — завершение реализации GRID-19 и SUM-VIS-01

Base: dac0985; this pass preserved concurrent functional changes. Canonical visual register: 244/253 CODE READY → 246/253; 5 PARTIAL, 2 BLOCKED. No DONE.

## GRID-19 — CODE READY (implementation)

- Explicit display annotations: previous 81 + 21 metal converter-backed + 6 contract KS = 108 in 17 XAML files. Readback of this pass confirms 27 XAML lines changed only by metadata.
- Common TableColumnMenu document fitter now handles approved simple scalar roles even in unannotated registered dynamic tables; dynamic right/center column roles also annotate themselves. Supports ordinary WPF StringFormat/culture, DataRowView schema fields, the three audited display-only converters (MetalAreaConverter, MetalPaidTextConverter, KsActValueConverter), stage distribution, KS per-act dynamic columns, actual inline stage displayed strings. No ConvertBack, arbitrary converter, nested property lookup or row realization.
- Inline estimate stage columns: cache by source/rows, effective font/DPI and stage version, respect manual changes, reset to defaults, include actual stage widths in viewport budget. Dynamic KS columns retain original defaults and same-key profile on rebuild, exact per-act ID, no user width capture from automatic layout. Separate summary/legacy fitters retain ownership.
- Default numeric and quantity columns expand for formatted amounts/headers; narrow viewport scrolls. Prose and arbitrary workbook-cell text retain intentional wrapping/ellipsis, tooltip/manual sizing; explicit user widths remain authoritative. These are deliberate design boundaries, not claims that arbitrary long user text must fit any viewport.
- Added WPF fixture for DataRowView dynamic schema and actual approved converters and source guards. Cache no longer materializes input arrays on unchanged layout passes.
- Commits: 163f276, 703f848, 5695f21, 52ab748, 0c4a962. Real geometry and build/test remain acceptance.

## SUM-VIS-01 — CODE READY (implementation)

- A01–A09, B01–B09, C01–C11 and D01–D06 accumulated implementation reviewed against current source and prior evidence. Active route uses the lazy embedded SummaryPanel for both KP and contract; supplier collapse, system multiselect, row keys, bands, shared styles, measured width and tiles remain present. Prior C12 source analysis explains separate work-unit price and material totals; verification on identical Pilot data is mandatory and no financial parity claim is made.
- Actual caller search found only private, orphan Summary_Click event handler (no subscriber in XAML); removed it rather than maintain two active routes. Compatibility CostSummaryWindow/Pane retained, previous width and selection/anchor improvements remain. Main summary pixel-only coordinate correction, recycled row identity, current source/detach/visibility guards. Source regression for route and pixel anchor.
- C12 and E01–E06 are acceptance/verification; any verified financial discrepancy is a separate defect, not a visual patch. Do not mark DONE until actual matched captures, signed/unsigned and same-document financial tests, WPF/SQL Pilot and legacy smoke.
- Commit 52ab748.

## Remaining implementation versus physical blockers

| ID | Status | Real remaining requirement |
|---|---|---|
| GRID-28 | PARTIAL | Excel-like cell selection in structural grids without changing the existing explicit row-command scopes; whole-row/full-row commands and current-cell/selection restoration must be redesigned together. |
| HOME-03 | PARTIAL | Native WPF H1 24 DIP/700 implemented; exact Qt −0.3px tracking is unsupported by TextBlock. Requires custom rendering or explicit approval of native shaping variance; no fake stretch. |
| SPACE-15 | PARTIAL | Remaining unjustified fixed dimensions, especially secondary dialogs and runtime monitor behavior. |
| SPACE-18 | PARTIAL | Remaining local style overrides, classify intended module-specific roles versus duplicates. |
| SPACE-19 | PARTIAL | Complete dynamic margin/padding review and shared resource replacements; retain intentional geometry. |
| QA-09 | BLOCKED | Real matched Python/C# reference captures on Windows and identical test data; tools ready, images absent. |
| QA-19 | BLOCKED | Real logo/tree screenshots and physical geometry inspection; fixture/matrix/crops ready, images absent. |

## Verification

Static readback/diff inspection; no compiled WPF, xUnit, App.Checks, Windows GUI/DPI, SQL Pilot or actual captures performed. Design pass changed no production DB, SQL, schema, finance, persistence format or release. Concurrent functional SQL commits were not touched. CODE READY is implementation status, not proof of actual successful build or visual/financial parity.
