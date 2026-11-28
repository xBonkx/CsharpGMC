# 28.11.2026 — estimate header, toolbar and table visual parity

Exactly ten independent canonical design IDs are CODE READY, not ten subtasks:
- HDR-01 — Actual one-line crumb order object/kind/company/lock/source warning; unknown company visible as in Python, long values trim with full tooltips. Header policy and source fixtures.
- HDR-02 — Qt root top18 and title gap2 preserved, 15pt title; responsive header aligns action group without extra warning row. Source metrics and WPF header geometry fixture.
- HDR-03 — HeaderText chooses nonblank annex title with legal title retained in tooltip, preserves intentional agreement/annex distinction, Python c.title comparison; four policy cases.
- HDR-20 — DocumentCrumbPanel compresses names; warning stays in same row; stable action breakpoint independent of metadata; normal/locked/warning and narrow/wide fixture.
- BAR-03 — Catalog/add split shown only for unlocked, non-rest, permitted active document; Python _fit_toolbar; existing handler revision/write guards retained. Policy/source cases.
- BAR-16 — ResponsiveToolbarPanel fits measured captions on one line, stable header breakpoint; 1920-width normal caption fixture with app scales 90–120%, narrow wrap fallback.
- GRID-11 — Shared Qt-family fallback and grid editor FontFamily binding, independent app/table scale; font-size policy and fixture.
- GRID-15 — Explicit Russian space/comma/minus/blank numeric display; price/work dashes and raw edit/formulas untouched; quantity/money cases.
- GRID-16 — Mode-driven work columns and nullable purchase preference; first load auto-detect purchase unless explicit user choice, reload preserves state; menu and toggle paths use same preference.
- FONT-03 — Common 10pt body and .95 header ratio applied once per zoom; grid editor binds grid font and size; app transform remains separate; scale matrix source and policy cases.

Python source references: smeta/ui/kp_editor.py _fill_header, _fit_toolbar, _sync_lock, _after_load, _purch_clicked, _sync_work_cols, _apply_zoom_style, _fill_pos; smeta/ui/common.py money/qty_text. The Python legal contract caption differs intentionally from the C# annex terminology requested for the new agreement-card model; no legal title, calculations, SQL, database or release changed. WPF adapts header breakpoint (420 DIP) and uses ordered font-family fallback; 96-DPI physical parity is not asserted. Automatic purchase visibility does not persist until the user chooses, legacy explicit true/false JSON stays valid. Signed/rest/permission visibility never replaces the existing command write guards.

Source changes and tests: 37 guarded substitutions before documentation; policy tests, actual source assertions, WPF fixture with production panel classes. No Windows/.NET execution, screenshots or paired DPI measurements here; actual acceptance is still required. HDR-02 is code-ready geometry alignment, not a claim that the earlier 12-pixel screenshot delta has been physically measured. BAR-16 WPF fixture checks normal toolbar captions; user-activated additional filters may wrap when insufficient width. Design 215/253 CODE READY, 38 PARTIAL; +10 canonical IDs.
