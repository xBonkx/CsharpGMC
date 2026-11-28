# 28.11.2026 — Header, toolbar and font-sensitive forms

Scope: eleven concrete implementation tasks (subtasks of existing HDR/ICON/SPACE/WIN IDs, not additional canonical defects). Source changes and regression checks are CODE READY; Windows runtime / actual paired screenshots are not DONE.

1. Header breadcrumb: Qt 10pt role, 300-DIP elision bound, complete tooltip (HDR-01).
2. Header vertical title gap 2 DIP per Python kp_editor.py titles.setSpacing(2) (HDR-02).
3. Signed status: vector lock, no platform-dependent emoji, original lock visibility (ICON-12).
4. Source mismatch: shared X geometry and plain caption, original visibility and warning color (ICON-12).
5. Estimate materials tab: Python materials SVG geometry, absolute independent contours and existing shared icon role (ICON-02).
6. Standalone cost toolbar: transport, mode switch and SMR markup receive vector captions; no commands changed (ICON-13). Truck is a WPF-specific icon, not copied from Python.
7. History search: natural height from shared search style instead of fixed 32 (WIN-24 / SPACE-15).
8. All contract acts search: min 32 via shared role instead of fixed host height (WIN-09 / SPACE-15).
9. Letters search and status filter can grow; refresh icon uses existing template (WIN-15 / SPACE-15 / ICON-13).
10. Orders responsible/date fields and multiline object name: minimum rather than fixed height, no 440-DIP mandatory combo, scroll for multiline (WIN-18 / SPACE-15).
11. Passports catalogue and section combo: shared minimum instead of fixed height (WIN-16 / SPACE-15).

Python source reference: gmcsmeta main e84fe86, smeta/ui/theme.py and kp_editor.py. The app.ico is already identical by Git blob SHA 5ac04bdc56f7491ff6273e6b76e9e912acec69d7 in Python and C#; MainWindow and csproj reference it. No binary changes.

Validation: 20 guarded replacements; all nine edited XAML files preserve names/commands and listed event bindings. Added HeaderAndCompactFieldsDesignTests (17 cases), not executed here. No actual Windows build, xUnit, WPF GUI or SQL tests. Canonical statuses remain unchanged pending broader audit (201/253 code ready, 52 partial); this is an implementation pass, not an assertion that broad whole-app tasks are finished. No SQL, DB, formulas, business logic or release changes.
