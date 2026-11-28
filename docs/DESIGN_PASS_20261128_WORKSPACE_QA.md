# 28.11.2026 — workspace and visual verification code readiness

Ten canonical IDs have implementation/check tooling at CODE READY (not witnessed PASS):
- HOME-36 — Bound card realization to 60 items/page, preserve document order/last create tile and current page on refresh, shrink cards below preferred width and wrap header actions. Production page fixture plus page-range tests.
- SPACE-22 — Responsive empty/object actions, shrinkable card/rename width, height-sensitive collapsible object totals with explicit override, bounded scroll/pinned actions in dynamic import/transfer/markup/section dialogs and compact system picker; prior adaptive toolbar/side panel retained.
- STATUS-05 — Compiled shell fixture asserts diagnostics occupy zero desired height and workspace remains within viewport across three logical sizes and application scales; real capture records monitor/work/frame bounds.
- QA-12 — Physical 1920x1080 capture code validates actual foreground window, monitor and frame bounds; paired Python/CSharp fixture metadata and PNG hash gate. Capture and review not run.
- QA-13 — Same physical capture path for 1366x768 and 2560x1440; mismatched resolution rejected, source/unit geometry cases. Capture and review not run.
- QA-14 — Per-monitor-aware capture, effective window and monitor DPI checks 96/120/144, rejects raster-only/incorrect DPI. Actual DPI not measured here.
- QA-15 — Separate capture cases normal/hover/selected/editing/disabled/focus for each page, actual foreground capture, existing interactive icon catalog; state is operator-prepared, not automatically inferred. Physical acceptance pending.
- QA-16 — Offline actual WPF edit, selection rectangle, copy/paste binding, sort and identity regression fixtures, existing numeric/formula paste, menu/column/keyboard and manual Pilot acceptance cases retained; physical mouse/keyboard and SQL readback not run.
- QA-17 — Opt-in real WPF layout performance harness uses 2500/10000/25000 rows, warmup +30 raw samples for each size and grid, top/last realization guard and production bounded card pages. No timing results claimed.
- QA-18 — RequireVisualCodeAcceptance gate requires clean HEAD, Release build, Core/WPF/export/layout/visual/capture validation steps all PASS, fails on missing/skipped checks. Does not publish or require SQL; actual build/tests not run.

Three IDs are production workspace/layout and its geometry check; seven are QA code readiness, not completed visual or runtime acceptance. No physical Windows session, no Python/CSharp screen captures, no .NET tests/build or performance measurements in this pass. QA-08 remains PARTIAL: historical _tools/audit_parity.ps1 and audit_snap are not in the current Git trees checked. QA-09/19 remain PARTIAL because actual Python/navigation screenshots are their deliverable. HOME-03 remains PARTIAL: WPF native text block does not implement Qt letter spacing. Other broad typography/spacing IDs remain PARTIAL despite additional targeted form changes.

Design choices: 60 cards per page for large lists (preserve last creation card and financial source count); 560 DIP short-height threshold for hiding totals unless operator overrides; compact forms preserve existing handlers. Geometry capture is an explicit operator-selected foreground window; metadata (fixture/role/state) is operator supplied, no verification of DB contents or input state, does not capture popup surfaces beyond the frame, never changes display settings. Screen capture requires Windows 10/11 and consented synthetic Pilot data; no SQL access. No user DB/settings mutations, calculation changes or release. New and updated test code authored, not executed. Canonical 235/253 CODE READY, 18 PARTIAL.

## Run locally (no SQL)

From C# checkout:
```powershell
powershell -NoProfile -File tools/local-qa/Invoke-LocalAcceptance.ps1 -RequireVisualCodeAcceptance
```

This gate checks automated code evidence only. Do not treat exit 0 as physical visual acceptance or release authorization. Logs stay local. For the physical matrix:
```powershell
python tools/local-qa/visual_capture.py matrix --out physical-cases.json
python tools/local-qa/visual_capture.py capture --pid 1234 --client Python --fixture python-fixture.json --resolution 1920x1080 --dpi 120 --out evidence/python-01
python tools/local-qa/visual_capture.py capture --pid 5678 --client CSharp --fixture csharp-fixture.json --resolution 1920x1080 --dpi 120 --out evidence/csharp-01
python tools/local-qa/visual_capture.py pair evidence/python-01/capture.json evidence/csharp-01/capture.json
```
PID examples are placeholders. Set actual Windows resolution and scaling yourself, open same synthetic data and prepare requested state within 5-second delay. Fixture example (replace values):
```json
{"page":"estimate","state":"normal","app_scale":1,"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","database":"GmcSmetaPilot_VisualFixture","synthetic_fixture_confirmed":true,"fixture_id":"F1","object_id":1,"document_id":2,"data_fingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","role":"editor","permissions":"write","signed":false,"smr_mode":"sum","columns_profile":"default","table_zoom":1}
```
Use actual full SHA and agreed data fingerprint, not the sample. Pair verifies metadata and PNG hashes only, follow with --visual-diff and human review. Captures contain actual on-screen content; inspect local files before sharing.
