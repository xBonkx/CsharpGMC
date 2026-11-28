#!/usr/bin/env python3
"""Capture one operator-selected real Windows window; no SQL, input injection, or display changes.

Use only synthetic Pilot data. Physical display/DPI and foreground PID must match
the requested case. A successful capture/pair is evidence, not visual acceptance.
"""
import argparse
import ctypes as C
import hashlib
import json
import re
import struct
import sys
import time
import zlib
from pathlib import Path
import design_audit

RESOLUTIONS = {(1366, 768), (1920, 1080), (2560, 1440)}
DPIS = {96, 120, 144}
STATES = ("normal", "hover", "selected", "editing", "disabled", "focus")
PAGES = ("navigation", "object-cards", "estimate", "materials", "cost-summary",
         "totals", "procurement", "shipments", "ks", "catalog", "metal",
         "pto-letters", "settings", "director")
NAV_STATES = ("year", "object", "folder", "kp", "contract", "annex",
              "stage-parent", "stage-child", "remainder", "archive", "deep-nesting")
SHA = re.compile(r"[0-9a-fA-F]{40}")
PNG = b"\x89PNG\r\n\x1a\n"

def png_size(path):
    with path.open("rb") as f:
        header = f.read(24)
    if len(header) != 24 or header[:8] != PNG or header[12:16] != b"IHDR":
        raise ValueError("not a PNG")
    return struct.unpack(">II", header[16:24])

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def validate_request(meta):
    if meta.get("page") not in PAGES:
        raise ValueError("unknown page")
    states = STATES + (NAV_STATES if meta["page"] == "navigation" else ())
    if meta.get("state") not in states:
        raise ValueError("unknown state for page")
    if type(meta.get("app_scale")) not in (int, float) or meta["app_scale"] not in (1, 1.1, 1.2):
        raise ValueError("unsupported app scale")
    if not SHA.fullmatch(str(meta.get("sha", ""))):
        raise ValueError("full source commit SHA required")
    if not re.fullmatch(r"GmcSmetaPilot_(?!Cutover)[A-Za-z0-9_]+", str(meta.get("database", ""))):
        raise ValueError("synthetic non-Cutover Pilot database only")
    if meta.get("synthetic_fixture_confirmed") is not True:
        raise ValueError("explicit synthetic_fixture_confirmed=true required")
    probe = dict(meta, dpi_x=96, dpi_y=96, window_width_px=100, window_height_px=100,
                 png="capture.png")
    errors = design_audit.validate_pair(probe, probe)
    if errors:
        raise ValueError("; ".join(errors))

def validate_geometry(resolution, dpi, monitor, window, actual_dpi):
    if tuple(resolution) not in RESOLUTIONS or dpi not in DPIS:
        raise ValueError("unsupported physical test case")
    ml, mt, mr, mb = monitor
    l, t, r, b = window
    if (mr - ml, mb - mt) != tuple(resolution):
        raise ValueError("physical monitor resolution differs")
    if actual_dpi != dpi:
        raise ValueError("effective window DPI differs")
    if not (ml <= l < r <= mr and mt <= t < b <= mb):
        raise ValueError("visible frame must lie within one monitor")

def png_bytes(width, height, bgra):
    if len(bgra) != width * height * 4:
        raise ValueError("invalid pixel buffer")
    rows = bytearray()
    for y in range(height):
        row = bgra[y * width * 4:(y + 1) * width * 4]
        rgb = bytearray(width * 3)
        rgb[0::3] = row[2::4]
        rgb[1::3] = row[1::4]
        rgb[2::3] = row[0::4]
        rows.extend(b"\0" + rgb)
    def chunk(name, data):
        return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data) & 0xffffffff)
    return PNG + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b"")

def capture_window(pid, resolution, dpi):
    if sys.platform != "win32":
        raise RuntimeError("real capture requires an interactive Windows desktop")
    from ctypes import wintypes as W
    u, g, d = C.WinDLL("user32", use_last_error=True), C.WinDLL("gdi32", use_last_error=True), C.WinDLL("dwmapi")
    def fn(dll, name, result, *args):
        f = getattr(dll, name)
        f.restype, f.argtypes = result, args
        return f
    handle = W.HANDLE
    class MonitorInfo(C.Structure):
        _fields_ = [("cbSize", W.DWORD), ("rcMonitor", W.RECT), ("rcWork", W.RECT), ("dwFlags", W.DWORD)]
    class BitmapInfo(C.Structure):
        _fields_ = [("size", W.DWORD), ("width", W.LONG), ("height", W.LONG),
                    ("planes", W.WORD), ("bitcount", W.WORD), ("compression", W.DWORD),
                    ("sizeimage", W.DWORD), ("xppm", W.LONG), ("yppm", W.LONG),
                    ("used", W.DWORD), ("important", W.DWORD), ("color", W.DWORD)]
    fg = fn(u, "GetForegroundWindow", W.HWND)
    process = fn(u, "GetWindowThreadProcessId", W.DWORD, W.HWND, C.POINTER(W.DWORD))
    is_iconic = fn(u, "IsIconic", W.BOOL, W.HWND)
    monitor_from = fn(u, "MonitorFromWindow", handle, W.HWND, W.DWORD)
    monitor_info = fn(u, "GetMonitorInfoW", W.BOOL, handle, C.POINTER(MonitorInfo))
    window_dpi = fn(u, "GetDpiForWindow", W.UINT, W.HWND)
    frame_bounds = fn(d, "DwmGetWindowAttribute", W.LONG, W.HWND, W.DWORD, C.c_void_p, W.DWORD)
    awareness = fn(u, "SetThreadDpiAwarenessContext", handle, handle)
    # The helper is a standalone CLI; make the process per-monitor aware before
    # querying monitor metrics, then explicitly set/restores the capture thread.
    set_process_awareness = fn(u, "SetProcessDpiAwarenessContext", W.BOOL, handle)
    set_process_awareness(C.c_void_p(-4))
    getdc = fn(u, "GetDC", handle, W.HWND)
    releasedc = fn(u, "ReleaseDC", C.c_int, W.HWND, handle)
    memdc = fn(g, "CreateCompatibleDC", handle, handle)
    deletedc = fn(g, "DeleteDC", W.BOOL, handle)
    dib = fn(g, "CreateDIBSection", handle, handle, C.POINTER(BitmapInfo), W.UINT,
             C.POINTER(C.c_void_p), handle, W.DWORD)
    select = fn(g, "SelectObject", handle, handle, handle)
    delete = fn(g, "DeleteObject", W.BOOL, handle)
    blt = fn(g, "BitBlt", W.BOOL, handle, C.c_int, C.c_int, C.c_int, C.c_int,
             handle, C.c_int, C.c_int, W.DWORD)
    flush = fn(g, "GdiFlush", W.BOOL)
    rect = lambda v: (v.left, v.top, v.right, v.bottom)
    previous = awareness(C.c_void_p(-4))  # PER_MONITOR_AWARE_V2 for capture thread only
    if not previous:
        raise OSError("cannot enter per-monitor DPI aware capture context")
    screen = memory = bitmap = old = None
    try:
        hwnd = fg()
        actual_pid = W.DWORD()
        if not hwnd or not process(hwnd, C.byref(actual_pid)) or actual_pid.value != pid or is_iconic(hwnd):
            raise ValueError("requested application PID is not the foreground visible window")
        info = MonitorInfo()
        info.cbSize = C.sizeof(info)
        monitor = monitor_from(hwnd, 2)
        if not monitor or not monitor_info(monitor, C.byref(info)):
            raise OSError("cannot read monitor bounds")
        frame = W.RECT()
        if frame_bounds(hwnd, 9, C.byref(frame), C.sizeof(frame)) != 0:
            raise OSError("cannot read physical extended frame bounds")
        actual_dpi = window_dpi(hwnd)
        bounds = rect(frame)
        validate_geometry(resolution, dpi, rect(info.rcMonitor), bounds, actual_dpi)
        # Compare actual monitor effective DPI as well; a DPI-unaware target is not a valid high-DPI fixture.
        sh = C.WinDLL("shcore")
        get_monitor_dpi = fn(sh, "GetDpiForMonitor", W.LONG, handle, C.c_int,
                             C.POINTER(W.UINT), C.POINTER(W.UINT))
        dx, dy = W.UINT(), W.UINT()
        if get_monitor_dpi(monitor, 0, C.byref(dx), C.byref(dy)) != 0 or (dx.value, dy.value) != (dpi, dpi):
            raise ValueError("monitor DPI differs or could not be verified")
        l, top, right, bottom = bounds
        width, height = right-l, bottom-top
        screen = getdc(None)
        if not screen:
            raise OSError("cannot access interactive desktop")
        memory = memdc(screen)
        if not memory:
            raise OSError("cannot allocate memory DC")
        bits = C.c_void_p()
        bi = BitmapInfo(C.sizeof(BitmapInfo)-C.sizeof(W.DWORD), width, -height, 1, 32, 0,
                        width*height*4, 0, 0, 0, 0, 0)
        bitmap = dib(memory, C.byref(bi), 0, C.byref(bits), None, 0)
        if not bitmap or not bits.value:
            raise OSError("cannot create capture bitmap")
        old = select(memory, bitmap)
        if not old or old == C.c_void_p(-1).value:
            old = None
            raise OSError("cannot select bitmap")
        if not blt(memory, 0, 0, width, height, screen, l, top, 0x40CC0020) or not flush():
            raise OSError("screen capture failed")
        after = W.RECT()
        if fg() != hwnd or frame_bounds(hwnd, 9, C.byref(after), C.sizeof(after)) != 0 or rect(after) != bounds:
            raise ValueError("foreground window moved or changed during capture")
        pixels = C.string_at(bits, width*height*4)
        return png_bytes(width, height, pixels), {
            "resolution": list(resolution), "dpi_x": actual_dpi, "dpi_y": actual_dpi,
            "monitor_dpi_x": dx.value, "monitor_dpi_y": dy.value,
            "window_width_px": width, "window_height_px": height,
            "frame_bounds_px": list(bounds), "monitor_bounds_px": list(rect(info.rcMonitor)),
            "work_bounds_px": list(rect(info.rcWork)), "foreground_pid": pid,
            "capture_kind": "PHYSICAL_WINDOWS_SCREEN"
        }
    finally:
        if old: select(memory, old)
        if bitmap: delete(bitmap)
        if memory: deletedc(memory)
        if screen: releasedc(None, screen)
        awareness(previous)

def capture(args):
    meta = json.loads(args.fixture.read_text(encoding="utf-8-sig"))
    validate_request(meta)
    if args.delay < 0 or args.delay > 30 or args.pid <= 0:
        raise ValueError("delay must be 0..30 and PID positive")
    resolution = tuple(map(int, args.resolution.split("x")))
    if resolution not in RESOLUTIONS or args.dpi not in DPIS:
        raise ValueError("unsupported display case")
    if args.out.exists():
        raise ValueError("evidence directory already exists")
    print("Switch to the requested synthetic app window; prepare the state now.", flush=True)
    time.sleep(args.delay)
    image, details = capture_window(args.pid, resolution, args.dpi)
    args.out.mkdir(parents=True, exist_ok=False)
    png = args.out / "capture.png"
    png.write_bytes(image)
    record = dict(meta, **details, client=args.client, png=png.name,
                  png_sha256=digest(png), status="CAPTURED_NOT_REVIEWED")
    (args.out / "capture.json").write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    print("CAPTURED_NOT_REVIEWED:", args.out)

def load_capture(path):
    meta = json.loads(path.read_text(encoding="utf-8-sig"))
    validate_request(meta)
    if meta.get("capture_kind") != "PHYSICAL_WINDOWS_SCREEN" or meta.get("status") != "CAPTURED_NOT_REVIEWED":
        raise ValueError("offscreen or unverified capture")
    if meta.get("png") != "capture.png" or not path.with_name("capture.png").is_file():
        raise ValueError("missing local capture PNG")
    png = path.with_name("capture.png")
    if png_size(png) != (meta["window_width_px"], meta["window_height_px"]) or digest(png) != meta["png_sha256"]:
        raise ValueError("capture dimensions/hash mismatch")
    validate_geometry(meta["resolution"], meta["dpi_x"], meta["monitor_bounds_px"],
                      meta["frame_bounds_px"], meta["dpi_y"])
    l, t, r, b = meta["frame_bounds_px"]
    if (r-l, b-t) != (meta["window_width_px"], meta["window_height_px"]):
        raise ValueError("physical frame and PNG dimensions differ")
    if (meta["monitor_dpi_x"], meta["monitor_dpi_y"]) != (meta["dpi_x"], meta["dpi_y"]):
        raise ValueError("monitor and effective DPI mismatch")
    return meta

def verify_pair(before_path, after_path):
    before, after = load_capture(before_path), load_capture(after_path)
    if (before["client"], after["client"]) != ("Python", "CSharp"):
        raise ValueError("pair must be Python then CSharp")
    errors = design_audit.validate_pair(before, after)
    if before["resolution"] != after["resolution"]:
        errors.append("physical resolution differs")
    if errors:
        raise ValueError("; ".join(errors))
    return {"status": "METADATA_MATCH_NOT_VISUAL_ACCEPTANCE", "python_sha": before["sha"],
            "csharp_sha": after["sha"], "resolution": before["resolution"],
            "dpi": before["dpi_x"], "page": before["page"], "state": before["state"],
            "python_png_sha256": before["png_sha256"], "csharp_png_sha256": after["png_sha256"]}

def matrix():
    return [{"page": page, "state": state, "resolution": f"{w}x{h}", "dpi": dpi,
             "app_scale": scale, "status": "NOT_RUN"}
            for page in PAGES for state in STATES + (NAV_STATES if page == "navigation" else ())
            for w,h in sorted(RESOLUTIONS) for dpi in sorted(DPIS)
            for scale in ((1,1.1,1.2) if (w,h)==(1920,1080) else (1,))]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    cap = sub.add_parser("capture")
    cap.add_argument("--pid", type=int, required=True)
    cap.add_argument("--client", choices=("Python", "CSharp"), required=True)
    cap.add_argument("--fixture", type=Path, required=True)
    cap.add_argument("--resolution", choices=("1366x768", "1920x1080", "2560x1440"), required=True)
    cap.add_argument("--dpi", type=int, choices=sorted(DPIS), required=True)
    cap.add_argument("--out", type=Path, required=True)
    cap.add_argument("--delay", type=int, default=5)
    pair = sub.add_parser("pair")
    pair.add_argument("python", type=Path)
    pair.add_argument("csharp", type=Path)
    mat = sub.add_parser("matrix")
    mat.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "capture":
        capture(args)
    elif args.command == "pair":
        print(json.dumps(verify_pair(args.python, args.csharp), ensure_ascii=False, indent=2))
    else:
        with args.out.open("x", encoding="utf-8") as f:
            json.dump(matrix(), f, ensure_ascii=False, indent=2)

if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, RuntimeError) as e:
        print("NOT ACCEPTED:", e, file=sys.stderr)
        sys.exit(1)
