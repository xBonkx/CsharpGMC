# 28.11.2026 — navigation and report readability

Main base reviewed: a419c57ce8f5e5432251b04b72a3cf2e2a123047.

## Implemented visual changes
- Year and object/folder navigation labels expose complete text via tooltip.
- Document name column explicitly shrinkable and name tooltip preserves full caption.
- Navigation date bounded to 76 DIP with ellipsis and full tooltip so unusual date strings cannot consume the entire document name area.
- Phase badge uses minimum 17×17 rather than fixed size; padding and natural height accommodate font fallback.
- Object-card metadata and navigation date use shared muted text role.
- Shell table row height is a 30-DIP minimum, not a fixed height; existing per-grid row policies remain intact.
- Shell table cell stretches across its row, content remains vertically centered.
- Report review/muted text respects the grid selection foreground, restoring semantic color on deselection.
- Full-value tooltips in all five shared report text styles (review, muted, strong-right, center, right).
- Common muted and disabled text brushes strengthened (#526174 / #607084). Against #F4F6FA calculated WCAG contrast ratios are 5.84:1 and 4.68:1; previously 4.40:1 and 2.37:1. White: 6.32:1 and 5.06:1. This is a deliberate readability deviation from the previous Python-aligned colors. Nontext disabled arrow and scrollbar brush remains #94A3B8.

## Evidence and limits
- Authored two offline WPF layout fixtures in VisualAuditChecks: real compiled shell tree templates with synthetic year, object/folder/archive labels, KP/contract/addendum, parent, stage, nested stage and remainder; selection markers, collapse/expand, header bounds at three sidebar widths. Folder and archive use existing object template (not separate SQL-driven modes). Second fixture checks four report text styles for selection/deselection and tooltip.
- Updated existing SidebarAgreementSourceTests for adaptive badge and date; added 9 source theory cases for contrast and report full text. Authored, not executed.
- Read back all changed production and test files; static XAML tag-balance checks passed for MainWindow.xaml, App.xaml and TypographyMetrics.xaml. Checked new affordance resource and references. No XAML compilation, Windows build, .NET test, physical DPI or mouse/keyboard run.
- NAV-20, GRID-19, GRID-28 (limited selection visual evidence), SPACE-15/18, FONT-10/13, QA-19 receive evidence but remain PARTIAL. The changes do not cover all grids or actual paired Python/Windows evidence. No canonical promotion; published registry remains 235/253 CODE READY, 18 PARTIAL.
- Chosen date cap and badge sizing provisional pending actual Windows check; disabled-text change intentional. No SQL, calculation, storage, release or version changes.

Local acceptance: Release build, Core xUnit, offline visual layout checks and paired Windows screenshots at 100/125/150% DPI. In particular test deep tree, narrow sidebar, long names/dates, review colors, selected cells with long values, shell table row growth at increased font, disabled text contrast and affordances. Do not use production database.
