# 28.11.2026 — Auxiliary design, icons and interactive catalog

Ten scoped CODE READY changes (not ten independent canonical IDs):

1. PТО list search and status filter grow to shared font minimum.
2. PТО list six archive/document actions get semantic vector icons.
3. PТО editor eight package/history/file actions get vector icons.
4. Letter attachment actions use original Python paperclip and shared open/edit/check/list.
5. Letter attachment movement buttons use existing vector arrows, keep 32-DIP hit targets and accessible captions.
6. Passport archive, system list, save, catalogue and add/delete point actions get shared vectors.
7. Metal stock category and search use natural font height; accessible search name.
8. Metal journal search uses matching leading search icon and reserved text inset, plus natural height and accessible name.
9. Metal operation/lot buttons receive semantic vectors; tags and handlers unchanged.
10. People company picker uses shared minimum height; bounded multiline footer intentionally retained.

Also added a genuinely interactive WPF icon catalog in the offline checks executable (--icon-catalog), with normal/disabled/checked/unchecked/hover/pressed/keyboard-focus states via actual mouse and keyboard interaction; static PNG output remains separate. Original app icon matching Python blob already verified in preceding pass; new source check covers XAML and project resource wiring. NAV-17 and ICON-15 code-ready; physical Windows acceptance remains. Other broad IDs stay PARTIAL rather than inflating counts.

48 guarded substitutions; name/handler/tag/binding attribute sets preserved for edited XAML; referenced shared icon templates verified against dictionary. Added 18 source-test cases, not run. Chosen semantic icons in auxiliary windows are WPF adaptations except Python paperclip and previously shared SVG; not an assertion of exact Python command-icon parity. No .NET/Windows/WPF execution, SQL, database, calculations, release or production data changes.
