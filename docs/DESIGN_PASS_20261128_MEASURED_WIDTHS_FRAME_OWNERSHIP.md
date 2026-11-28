# 28.11.2026 — measured grid widths and frame ownership

Scope: design only. Starting design status 243/253 CODE READY, 10 PARTIAL. Concurrent functional commits were preserved.

## SPACE-11 — source implementation CODE READY

Reviewed all 46 application XAML files at 4221bcda9b4816f92859b2900235934f78d75d59 using a lexical tag-stack inventory following the existing design_audit.py nested inset criteria. No unmatched tags in this scan. 13 indirect padded/styled frame candidates, all in three files:
- EstimateView.xaml: 4 header chips and 4 footer tiles. Outer transparent Border nodes owned only page spacing, not a visible frame. Converted the header to its existing ResponsiveToolbarPanel with Margin and the footer to its existing DockPanel with Margin. Preserved expanded/compact footer resource values and all control names, actions and bindings. Transport/PNR rows also no longer have an empty Border around their Grid. New LayoutFooterFieldGap preserves 0,2,0,0.
- AllContractActsWindow.xaml: four independent colored amount cards inside the white report surface. Outer padding16 and KsCard padding have different roles; retain.
- MainWindow.xaml: sidebar outer padding and navigation segment's padding3 have different roles. Retain the three-mode selector.
- Dynamic Border search: reviewed explicit construction sites in HelpDialog, PromptDialog, EstimateDiscountDialog, SummarySystemPickerPopup, EstimateView.Import.Structured, EstimateView.InlineStages.Visual, EstimateView.Sections and EstimateView.Materials. Distinct content/preview cards, popup list frame, dialog chrome, separators and glyph decorations are intentional; no blind removal of visible frames. This was indexed-source review, not exhaustive C# dataflow or runtime template expansion.

Only EstimateView.xaml and LayoutMetrics.xaml changed among XAML files between audited head and this pass. Readback: 110 named elements, 49 event attributes, 77 binding expressions and 13 sort keys identical; all 14 referenced Layout* keys present. Added whole-XAML FrameOwnershipSourceTests: five reviewed independent inset pairs only, unknown new pairs fail. Existing header/footer source guards updated; stale expander width assertion 16→22 corrected to actual XAML.
Commits 4119a34, 213e4f4, 56c3c40. CODE READY for source design implementation; compiled WPF template expansion and real DPI/visual acceptance remain.

## GRID-19 / SUM-VIS-01 — PARTIAL, implementation expanded

- EstimateView.ColumnLayout: in automatic mode measure actual displayed row numbers, units, quantities, money, work and area strings; header words also contribute minima. Name, note and normative references retain existing wrapping/flex policy. Preserve manual widths, active edit geometry, inline-stage budget and horizontal scrolling when readable minima exceed viewport. Cache measurements by weak source and row-list identity, effective font/DPI and visible column/header signature. Area recalculation invalidates; optimistic decimal overflow does not crash layout. Bounded per-pass text/glyph cache (4096 distinct text values), no row realization.
- LegacyCostColumnFit: five legacy cost/SMR/expense/summary/totals grids use their already formatted string DTO values and original default width floor. Only simple, converter-free allowlisted bindings; valid manual preferences, textual flex columns and excluded columns unaffected. Coalesced layout, weak source identity, font/DPI, drag suspension and reset handling. Scroll rather than change calculations or storage.
- Embedded SummaryColumnFit observes replacement source even when viewport/header/font unchanged.
- Added WPF fixture measurements for large signed amounts, unaffected neighboring columns, legacy work unit semantics, totals/expense strings, expander width and font changes; source assertions.
- Commits c788a2a, e2c1df9, c924577, 1b7773b, 1eecba4, 9c1e867, a6211bf, 4221bcd, 21e36d6.

Remaining: other grids (procurement, KS, shipments and auxiliary reports), actual large-number/DPI geometry, real legacy and embedded summary scroll/anchor, C12 and E01–E06. Do not mark GRID-19/SUM-VIS-01 fully ready from this scope alone.

## Verification and limits

Static readback and lexical XAML tag checks only. Added source and WPF regression cases, not executed. No .NET/Windows build, xUnit, App.Checks, SQL Pilot or physical captures executed. No changes to SQL, schema, production DB, financial calculations, write paths or release. The deliberate decision is to keep separately styled report cards/segment controls, preserve native text shaping, and never override explicit manual widths. Original DONE checkboxes stay open until real acceptance.

Design +1 CODE READY: SPACE-11. Current 244/253 CODE READY, 9 PARTIAL.
