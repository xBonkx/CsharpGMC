# 28.11.2026 — document width and responsive picker pass

Starting and ending canonical design count: 244/253 CODE READY, 9 PARTIAL. This pass adds implementation evidence for five existing IDs; no premature promotion.

## GRID-19
- New DocumentColumnFit opt-in via TableColumnMenu.FitDisplayPath / FitExtra. Explicit simple DTO scalar display properties only. StringFormat follows ordinary WPF composite or standard format with binding/grid culture. Text-column annotations must match the existing binding; template annotation points to displayed text, not the editor. No converter invocation, nested property lookup, SQL, write path or row-container realization.
- 81 annotations in 16 XAML files: KS editor 9; procurement/history 11; shipments 6; estimate materials 10; estimate totals 4; KS3 previous 1; group KS reports 9; all-contract acts 4; comparison 6; system SMR 1; trash 3; PTO 3; passports 2; letters 3; history 1; metal stock/history 8.
- Preserve explicit valid user widths, flex/star widths, excluded or hidden columns, initial width floors and active editors. Coalesce on dispatcher, invalidate collection replace/add/remove/reset and commit, use weak source identity and font/DPI signature. Existing header resize/reset mechanisms extended.
- Static diff readback confirms all 81 changed XAML lines are identical after stripping the new metadata; all original bindings/styles remain. WPF fixture coverage for signed amounts, standard/composite formats, null/date and badge label. Source test pins all annotated columns to display binding and profile guards.
- Remaining: converter-backed metal numeric columns, dynamic/other template surfaces, actual long-value viewport/DPI geometry.

## SPACE-15/18/19 and SUM-VIS-01
- PromptDialog resizable with wrapping pinned actions, common footer inset and action gap. MaterialsMarkupDialog shared choice/group margins and a minimum width. SummarySystemPickerPopup gets actual anchor monitor work area via DynamicFormLayout.WorkAreaFor, reapplies at open, keeps family/choice behavior and pinned actions; shared caption/field/choice/popup insets. No literal Margin/Padding remains in those three dynamic files. Structural border strokes retained.
- Readback confirmed all referenced Layout* keys exist. Updated monitor source guard. No real multi-monitor or keyboard/selection testing.

## Commits and verification
d960a48, 687e1aa, 68e0699, e18286f, 80b12b1, 9a5169d. Static readback only; added tests not run. No Windows .NET build, App.Checks, SQL Pilot or physical screenshot capture. No change to production DB, schema, finance, persistence or release. Deliberate scope: do not infer converter display or override manual widths; physical QA remains pending.
