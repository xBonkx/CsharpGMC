# 28.11.2026 — Auxiliary design and interactive catalog (final corrected scope)

Ten scoped implementation changes CODE READY, not ten independent canonical IDs:
1. PТО list search grows naturally; status selector auto width within 155–240 and full text tooltip.
2. PТО editor status selector uses shared min height, flexible width and full tooltip.
3. Letter attachment move arrows use shared vector with unchanged 32-DIP hit target and accessible name.
4. Letter sender and recipient fields grow from 110 to 240 DIP with scroll, instead of fixed height.
5. Letter attachment role selector can grow 180–320 DIP and exposes complete text.
6. Passport form label column adapts to width, catalogue and section selectors expose full text.
7. Metal stock category and search use natural height; label/category stay grouped, search wraps separately.
8. Metal journal search has a leading search icon, matching inset, accessible name and natural height.
9. People history selector no longer requires fixed 475 DIP, exposes complete selected label.
10. People company selector uses shared minimum height and full name tooltip.

Also: real interactive --icon-catalog for normal/disabled/checked/unchecked/hover/pressed/keyboard focus; actual input, not a simulated screenshot. Original app.ico verified by blob SHA and project wiring source test. NAV-17/ICON-15 CODE READY, Windows acceptance pending.

Correction after comparing gmcsmeta main smeta/letters/ui.py, passports/ui.py, pto/ui.py and ui/metal_warehouse.py: text-only commands must stay text-only. Reverted the newly added speculative caption icons and unused templates from a6ec37a; kept previously approved icon choices and accessible vector arrows. Updated obsolete OfficeDocumentVisualTests assertion to distinguish prior production icons from text-only commands (the preceding AuxiliaryWindowPolishSourceTests explicitly requires some of those icons). Width/height bounds are WPF adaptations, not pixel-exact Qt claims.

Protected XAML names/handlers/tags unchanged; new 17 source cases not run (six field heights, two arrow cases, five text-only commands, two multiline fields, one wrap and one app icon). No Windows/.NET build, real WPF, SQL, database, formulas or release changes. Canonical 203/253 CODE READY, 50 PARTIAL.
