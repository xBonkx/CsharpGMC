"""Offline startup correlation. No process launch, network or database access."""
import argparse
import csv
import json
from pathlib import Path


def summarize(directory, launcher_pid, start_ticks, frequency):
    events = []
    metrics = []
    for pattern, target in (("startup-*.csv", events), ("performance-*.csv", metrics)):
        for path in sorted(Path(directory).glob(pattern)):
            with path.open(encoding="utf-8-sig", newline="") as stream:
                target.extend(csv.DictReader(stream))
    def select(rows, pid):
        return sorted(
            (r for r in rows if int(r["ProcessId"]) == pid
             and int(r["Frequency"]) == frequency
             and int(r["MonotonicTicks"]) >= start_ticks),
            key=lambda r: int(r["MonotonicTicks"]),
        )
    launch = select(events, launcher_pid)
    if not any(r["Phase"] == "launcher_start" for r in launch):
        raise ValueError("Launcher trace missing or clock mismatch")
    lm = select(metrics, launcher_pid)
    children = {int(r["Value1"]) for r in lm if r["Phase"] == "launcher_child_pid"}
    if len(children) != 1:
        raise ValueError("Exactly one correlated application PID required")
    app_pid = children.pop()
    app = select(events, app_pid)
    if not app:
        raise ValueError("Application trace missing")
    def elapsed(row):
        return round((int(row["MonotonicTicks"]) - start_ticks) * 1000 / frequency, 3)
    def phases(rows):
        return [{"phase": r["Phase"], "since_launch_ms": elapsed(r)} for r in rows]
    def measured(rows):
        return [{"phase": r["Phase"], "duration_ms": float(r["DurationMs"]),
                 "since_launch_ms": elapsed(r)} for r in rows]
    ready = next((elapsed(r) for r in app if r["Phase"] == "main_workspace_ready_rendering"), None)
    return {"launcher_pid": launcher_pid, "app_pid": app_pid,
            "ready_rendering_ms": ready, "complete": ready is not None,
            "launcher": phases(launch), "application": phases(app),
            "launcher_metrics": measured(lm), "application_metrics": measured(select(metrics, app_pid))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--launcher-pid", type=int, required=True)
    parser.add_argument("--start-ticks", type=int, required=True)
    parser.add_argument("--frequency", type=int, required=True)
    args = parser.parse_args()
    if args.frequency <= 0 or args.launcher_pid <= 0 or args.start_ticks < 0:
        parser.error("Invalid clock or PID")
    try:
        result = summarize(args.directory, args.launcher_pid, args.start_ticks, args.frequency)
    except (OSError, ValueError, KeyError) as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2))
    return 0 if result["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
