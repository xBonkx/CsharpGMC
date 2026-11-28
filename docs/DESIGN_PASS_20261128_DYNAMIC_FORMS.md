# 28.11.2026 — Dynamic form design code-ready pass

Ten scoped design implementation changes:
1. Shared resource-based dynamic dialog font family/size and display/ClearType/rounding; work-area cap for seven dialogs.
2. Common one-inset scrolling form body and independent pinned footer in six dialogs.
3. Markup heading uses H2 and natural wrapping; radio/checkbox captions wrap.
4. Markup coefficient/percent arranged as two form rows, matching Python cost_view.MarkupDialog QFormLayout, with original defaults and callbacks.
5. Estimate discount preview natural line height, muted semantic text and proportional columns.
6. KS adjustment dialog shared heading, wrapping labels, standard actions and pinned footer.
7. Transport dialog wraps supplier captions, headings and keeps save/cancel outside long supplier list.
8. KS properties proportional label/input widths, growing multiline DS field with bounded scrolling.
9. Section selection no 380-DIP fixed minimum; complete selection tooltip and shared field height.
10. KS section amount grid semantic text/money alignment, virtualized columns and standard actions, existing 36-DIP minimum and bindings retained.

Shared helper is presentation-only; no changes to calculations, commit handlers, SQL or database. WorkArea is primary Windows work area (per-monitor acceptance pending); width ratios and multiline bounds are WPF adaptations. Added 11 source cases and an offline WPF layout fixture. Checks authored, not run; static guarded substitutions (34); no Windows/.NET build. Broad FONT/SPACE/GRID statuses remain PARTIAL because this pass covers seven dialogs, not all screens. Canonical design count unchanged by this pass.
