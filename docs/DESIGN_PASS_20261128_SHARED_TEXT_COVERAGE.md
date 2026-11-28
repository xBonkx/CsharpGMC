# 28.11.2026 — shared text role coverage and compact dialogs

Baseline reviewed: 98882cab5085c113094c4eac9576e3bac608cfc4. Direct main changes; no calculation or database changes.

## Implementation

- 34 XAML files changed across primary shell, estimate and stages, cost and totals, summary, materials, procurement, shipments, KS and reports, agreement cards, reference windows, letters, PTO, passports, orders, metal, people, history, trash, comparison/transfer/replacement and deletion dialogs.
- 119 direct TextBlock foreground instances previously using #64748B or #94A3B8 now use the shared UiMutedTextBrush. Local text style setters in DirectorView, EstimateView, ProcurementPane, ShipmentsPane, EstimateMaterialsPanel and EstimateSummaryPanel also use shared roles. Disabled estimate menu and shipment date field use UiDisabledTextBrush; icon strokes and distinct status palettes retained.
- Four dynamic estimate C# files use shared secondary contrast for stage card status/empty state, Excel confirmation messages and totals toggle. Warning color, state dot and selection behavior remain unchanged.
- Totals font role centralized in both CostSummaryPane and EstimateTotalsPanel; existing 14 DIP preserved.
- StageMoneyDialog: bounded height, scrollable fields and explanatory text, pinned wrapping confirmation actions, removed extra spacer row. Width/minimum height choice is provisional.
- DeletePositionsDialog: work-area cap, one scrolling body, pinned confirmation actions, wrapping complete preview names and shared typography. Existing preview limit of eight names, additional-count indication and deletion behavior retained.

## Verification

- Enumerated all 46 XAML files from the GitHub tree; read and checked each for balanced markup tags and zero direct TextBlock foreground literals #64748B/#94A3B8. After removal of the nested scrolling list in DeletePositionsDialog, the single scrolling body and pinned footer were checked structurally. This is a static lexical check, not a WPF compilation.
- Added SharedTextRoleCoverageTests.cs: six source test cases (whole app XML tree, two dialog scroll/footer cases, two totals typography cases, deletion preview wrapping and tooltip). Authored, not run.
- Existing relevant tests were searched for exact old colors; dedicated semantic glyph/status assertions intentionally preserved. Search index can lag main; full test run not performed.
- FONT-10 gets whole-XAML direct label coverage and additional local/dynamic changes; FONT-08/12/13 and SPACE-14/15/18/19 receive partial implementation evidence. GRID-19 receives limited deletion preview text evidence, not actual grid completion. No canonical promotion: full dynamic UI and semantic disabled text review, paired Windows rendering and compilation not yet completed.
- Prior 235/253 CODE READY and 18 PARTIAL unchanged by this pass. Do not count individual screens as canonical closed IDs.
- Decisions: shared muted role for ordinary placeholders and captions, deliberately stronger than old Python-aligned palette. Specialized status colors and inactive glyph colors retained. The deletion and stage-money work-area caps follow existing primary monitor pattern, not a new per-monitor placement algorithm.

No Windows build, .NET tests, real screenshots or SQL access. Local acceptance: Release build, Core xUnit, offline WPF checks, then actual Windows 100/125/150% DPI, long names, narrow forms, scroll and selection, disabled states, paired Python comparison on synthetic data. No version or release changes.
