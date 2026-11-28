# 28.11.2026 — compact dialogs follow-up

Presentation-only changes on main, reviewed from 9d3b95c86d7c672cb6c5a6cb3e060a5c5c0c7fd8.

| UI surface | Change |
|---|---|
| Single-field prompt | Shared heading, secondary caption, form margin, field height and inset. |
| KS-2 section closing | Shared explanation and list spacing; wrapping selection actions; distinct confirm/cancel controls. |
| Excel append progress | Content-sized window, shared chrome and cancel style; original cancellation retained. |
| Excel append preview | Lower minimum width/height, wrapping footer, full section names and shared detail inset. |
| Annex transfer | Semantic destination caption, accessible picker name, wrapping actions. |
| SMR markup prompt | Flexible percent field, accessible name, shared action gap and localized apply caption. |
| Excel paste confirmation | Preview names wrap instead of ellipsis, raw tooltip retained; shared dynamic font chrome. |
| Excel paste error | Bounded error text with vertical-only scroll and shared font chrome. |
| Summary system picker | Counter gets own line, footer actions wrap; shared muted placeholder and named search field. |
| Archive year dialog | Scrollable body bounded by work area, pinned wrapping actions, smaller minimum width and semantic captions. |
| Object properties | Shared heading and secondary information, wrapping footer and shared action gap. |
| Delete confirmation | Shared form inset and explanatory text roles; danger style and original confirmation remain. |
| Document creation/edit | Shared heading, VAT and tag hint roles; empty VAT hint still collapses; wrapping action footer. |
| Global search | Wrapped result template, stretched containers, horizontal scrolling disabled, full result tooltip remains; shared chrome roles. |

Shared helpers: DynamicFormLayout.Secondary, centered natural-height caption wrapping and shared zero inset; named dialog spacing resources in Theme/LayoutMetrics.xaml; shared heading/hint helpers in WorkspaceQueryWindows.cs.

13 source regression cases authored in CompactDynamicFormSourceTests.cs (8 dynamic surface theories, 4 XAML footer theories, 1 resource check). Read back changed production files from main, checked new layout resource names are defined, shared heading/secondary styles exist, and XAML start/end tags are balanced. These are narrow static checks, not a XAML compilation or WPF runtime test.

SPACE-11/14/15/18/19 and FONT-08/10/12/13 receive additional implementation evidence, but remain PARTIAL until the remaining app-wide scope is audited. GRID-19 receives limited long-label improvements outside actual grids; not promoted. No canonical status promotion; 235/253 CODE READY and 18 PARTIAL unchanged from reviewed registry. No claim of ten completed canonical IDs.

Choices: shared role spacing applied to touched forms only; lower minimums for append preview (420×360) and archive dialog (320 width) provisional; archive bounds use existing primary work-area pattern, not new per-monitor placement. Disabled text palette, financial rules, SQL, database, version and release unchanged. No Windows build, .NET test run, visual comparison, real DPI or SQL Pilot run.

Acceptance: Release build, Core xUnit + WPF offline checks; open the surfaces with long Russian labels at 100/125/150% DPI, narrow windows, keyboard navigation and original cancel/confirm behavior. Inspect actual text wrap, contrast, controls, scroll and remaining viewport. Use only synthetic/test data if a database is necessary.
