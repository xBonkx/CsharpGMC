"""Offline before/after matrix. No SQL, credentials, subprocess or networking.

Manifest is JSON with "pairs": [{ "scenario": str, "before": relative metadata
JSON, "after": relative metadata JSON }] or an explicitly unmeasured row with
"status": "OPEN"|"PARTIAL"|"BLOCKED", "reason": str. Metadata refers to raw CSV
and independent correctness evidence, relative to the manifest directory.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import sys

KEYS = ("scenario", "client", "dataset", "machine_profile", "topology", "cache",
        "build", "runtime", "os", "sql_version", "schema", "rows", "contracts", "dpi", "resolution")
METRICS = ("elapsed_ms", "sql_count", "ui_stall_ms", "private_bytes", "working_set_bytes",
           "managed_bytes", "cpu_ms", "retained_bytes", "bytes_received")
TOPOLOGIES = {"LOCAL", "LAN", "WIFI", "VPN", "SLOW", "OTHER"}
SHA = re.compile(r"[a-fA-F0-9]{40}|[a-fA-F0-9]{64}")


def local_file(root, name):
    if not isinstance(name, str) or not name or Path(name).is_absolute():
        raise ValueError("relative evidence path required")
    p = (root / name).resolve(strict=True)
    if not p.is_relative_to(root) or not p.is_file() or p.stat().st_size == 0:
        raise ValueError("evidence outside run or empty")
    return p


def digest(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def number(text):
    v = float(text)
    if not math.isfinite(v) or v < 0:
        raise ValueError("invalid metric")
    return v


def distribution(values):
    data = sorted(values)
    rank = lambda p: data[math.ceil(len(data) * p) - 1]
    return {"count": len(data), "min": data[0], "p50": statistics.median(data),
            "p95": rank(.95), "p99": rank(.99) if len(data) >= 100 else None, "max": data[-1]}


def load_run(root, relative):
    file = local_file(root, relative)
    meta = json.loads(file.read_text(encoding="utf-8-sig"))
    if not SHA.fullmatch(meta["sha"]) or any(k not in meta for k in KEYS):
        raise ValueError("missing SHA/metadata")
    if meta["topology"] not in TOPOLOGIES or meta["build"] not in ("Release", "Debug"):
        raise ValueError("invalid topology/build")
    if meta["client"] not in ("CSharp", "Python") or meta["rows"] < 0 or meta["contracts"] < 1:
        raise ValueError("invalid client/scope")
    for key in KEYS:
        if meta[key] is None or meta[key] == "" or meta[key] == "UNSPECIFIED":
            raise ValueError("incomplete comparison key")
    if not isinstance(meta["required_metrics"], list) or "elapsed_ms" not in meta["required_metrics"] or \
            not set(meta["required_metrics"]).issubset(METRICS):
        raise ValueError("invalid required metrics")
    if meta["correctness"] not in ("PASS", "FAIL", "UNVERIFIED"):
        raise ValueError("invalid correctness")
    proof = None
    if meta["correctness"] != "UNVERIFIED":
        p = local_file(root, meta["correctness_evidence"])
        proof = {"path": meta["correctness_evidence"], "sha256": digest(p)}
    raw = local_file(root, meta["raw"])
    with raw.open(encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        required = {"iteration", "status", *METRICS}
        if reader.fieldnames is None or not required.issubset(reader.fieldnames) or \
                len(reader.fieldnames) != len(set(reader.fieldnames)):
            raise ValueError("raw CSV schema mismatch")
        rows = list(reader)
    runs = meta["runs"]
    if not isinstance(runs, int) or not 30 <= runs <= 300 or len(rows) != runs + 1:
        raise ValueError("need warmup and 30-300 measured raw samples")
    values = {k: [] for k in METRICS}
    outcomes = {"FAIL": 0, "TIMEOUT": 0, "CANCELLED": 0}
    for i, row in enumerate(rows):
        if int(row["iteration"]) != i:
            raise ValueError("wrong iteration order")
        status = row["status"]
        if status in outcomes:
            outcomes[status] += 1
            continue
        if status != ("WARMUP" if i == 0 else "OK"):
            raise ValueError("wrong iteration status")
        for key in METRICS:
            if row[key] not in ("", None):
                val = number(row[key])
                if i > 0:
                    values[key].append(val)
    distributions = {k: distribution(v) if len(v) == runs else None for k, v in values.items()}
    return {"meta": meta, "source": relative, "raw": meta["raw"], "raw_sha256": digest(raw),
            "proof": proof, "outcomes": outcomes, "metrics": distributions}


def compare(root, pair, limit):
    scenario = pair["scenario"]
    if "before" not in pair or "after" not in pair:
        if "before" in pair or "after" in pair:
            raise ValueError("both runs required")
        if pair.get("status") not in ("OPEN", "PARTIAL", "BLOCKED") or not pair.get("reason"):
            raise ValueError("unmeasured rows need status and reason")
        return {"scenario": scenario, "status": pair["status"], "reason": pair["reason"]}
    a, b = (load_run(root, pair[k]) for k in ("before", "after"))
    x, y = a["meta"], b["meta"]
    if any(x[k] != y[k] for k in KEYS) or x["scenario"] != scenario or \
            x["runs"] != y["runs"] or set(x["required_metrics"]) != set(y["required_metrics"]) or \
            a["raw_sha256"] == b["raw_sha256"]:
        raise ValueError("incomparable or reused raw evidence")
    failures = sum(a["outcomes"].values()) + sum(b["outcomes"].values())
    missing = []
    regressions = []
    deltas = {}
    for key in METRICS:
        first, second = a["metrics"][key], b["metrics"][key]
        if first is None or second is None:
            if key in x["required_metrics"]:
                missing.append(key)
            continue
        deltas[key] = {}
        for percentile in ("p50", "p95", "p99"):
            old, new = first[percentile], second[percentile]
            if old is None or new is None:
                continue
            pct = (new - old) / old * 100 if old else (0 if not new else None)
            deltas[key][percentile] = pct
            if (pct is None and new > old) or (pct is not None and pct > limit):
                regressions.append(key + ":" + percentile)
    correctness = "PASS" if x["correctness"] == y["correctness"] == "PASS" else \
        "FAIL" if "FAIL" in (x["correctness"], y["correctness"]) else "UNVERIFIED"
    status = "BLOCKED" if failures or regressions or correctness == "FAIL" else \
        "PARTIAL" if missing or correctness != "PASS" else "DONE"
    return {"scenario": scenario, "status": status, "reason": "measured comparison",
            "before": a, "after": b, "missing_metrics": missing, "regressions": regressions,
            "correctness": correctness, "deltas_pct": deltas}


def build(manifest, limit):
    root = manifest.resolve().parent
    data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    if not math.isfinite(limit) or not 0 <= limit <= 1000 or not data["pairs"]:
        raise ValueError("invalid threshold or empty manifest")
    result = []
    seen = set()
    for pair in data["pairs"]:
        name = pair["scenario"]
        if not isinstance(name, str) or not name or name in seen:
            raise ValueError("duplicate or empty matrix row")
        seen.add(name)
        result.append(compare(root, pair, limit))
    return {"format": 1, "threshold_pct": limit, "rows": result,
            "status": "DONE" if all(r["status"] == "DONE" for r in result) else "INCOMPLETE"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--max-regression-pct", type=float, default=10)
    args = parser.parse_args()
    try:
        report = build(args.manifest, args.max_regression_pct)
        with args.output.open("x", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=2, allow_nan=False)
        return 0 if report["status"] == "DONE" else 3
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print("Matrix rejected:", type(exc).__name__, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
