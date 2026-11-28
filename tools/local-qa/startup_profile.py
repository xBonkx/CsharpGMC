"""Analyze a single startup trace, keeping external profiler evidence separate."""
import argparse
import csv
import json
from pathlib import Path

PAIRS = (
    ("application_ctor", "app_application_ctor_begin", "app_application_ctor_end"),
    ("application_resources", "app_application_initialize_begin", "app_application_initialize_end"),
    ("wpf_run_to_startup", "app_run_begin", "app_startup_enter"),
    ("branding", "app_branding_begin", "app_branding_end"),
    ("interaction_setup", "app_interaction_setup_begin", "app_interaction_setup_end"),
    ("window_ctor", "app_main_window_ctor_begin", "app_main_window_ctor_end"),
    ("window_xaml", "main_initialize_component_begin", "main_initialize_component_end"),
    ("shell_setup", "main_shell_setup_begin", "main_shell_setup_end"),
    ("settings_font_scale", "main_settings_apply_begin", "main_settings_apply_end"),
    ("pto_menu", "main_pto_menu_begin", "main_pto_menu_end"),
    ("content_to_ready_render", "app_main_content_rendered", "main_workspace_ready_rendering"),
)
OPTIONAL = (("icon_decode", "app_icon_decode_begin", "app_icon_decode_end"),)


def analyze(path, flavor, r2r, cache, external_profile=None):
    with Path(path).open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError("Empty trace")
    pids = {int(row["ProcessId"]) for row in rows}
    frequencies = {int(row["Frequency"]) for row in rows}
    if len(pids) != 1 or len(frequencies) != 1 or min(pids) <= 0 or min(frequencies) <= 0:
        raise ValueError("Trace must contain exactly one process and clock")
    frequency = frequencies.pop()
    phases = {}
    for row in rows:
        ticks = int(row["MonotonicTicks"])
        if ticks < 0:
            raise ValueError("Negative timestamp")
        phases.setdefault(row["Phase"], []).append(ticks)

    def single(name):
        values = phases.get(name, [])
        return values[0] if len(values) == 1 else None

    entry = single("app_process_entry")
    ready = single("main_workspace_ready_rendering")
    if entry is None:
        raise ValueError("Missing or repeated managed application entry marker")
    if ready is not None and ready < entry:
        raise ValueError("Ready marker precedes managed entry")
    builds = [name for name in ("debug", "release") if single("trace_build_" + name) is not None]
    build = builds[0] if len(builds) == 1 else None
    def duration(start, end):
        first, last = single(start), single(end)
        if first is None or last is None or first < entry or last < first:
            return None
        return round((last - first) * 1000 / frequency, 3)
    timings = {name: duration(start, end) for name, start, end in PAIRS + OPTIONAL}
    missing = [name for name, _, _ in PAIRS if timings[name] is None]
    return {
        "trace": str(path), "process_id": pids.pop(), "build": build,
        "flavor": flavor, "ready_to_run": r2r, "cache_condition": cache,
        "managed_entry_to_ready_ms": None if ready is None else round((ready - entry) * 1000 / frequency, 3),
        "phase_ms": timings, "missing_required_phases": missing,
        "complete": ready is not None and build is not None and not missing,
        "external_profile": str(external_profile) if external_profile else None,
        "external_cpu_jit_disk_antivirus_attribution": "NOT_ANALYZED",
        "note": "Nested phases overlap. Render callback is not physical display. Flavor/R2R/cache are operator labels.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace", type=Path)
    parser.add_argument("--flavor", choices=("fx", "sc"), required=True)
    parser.add_argument("--r2r", choices=("on", "off", "unknown"), required=True)
    parser.add_argument("--cache", choices=("cold", "warm", "unknown"), required=True)
    parser.add_argument("--external-profile", type=Path)
    args = parser.parse_args()
    if args.external_profile is not None and not args.external_profile.is_file():
        parser.error("External profiler artifact does not exist")
    try:
        result = analyze(args.trace, args.flavor, args.r2r, args.cache, args.external_profile)
    except (OSError, ValueError, KeyError) as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2))
    return 0 if result["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
