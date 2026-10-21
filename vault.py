#!/usr/bin/python3
"""Screenshot Vault: watches the screenshot inbox, copies each new capture to the
clipboard, files it under archive/YYYY-MM/YYYY-MM-DD with a readable name, and
serves a local gallery at http://localhost:7333.

Usage:
  vault.py serve                 run watcher + gallery server (launchd runs this)
  vault.py import FILE [FILE...] move existing screenshots into the archive
"""
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from datetime import datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlparse

HOME = os.path.expanduser("~")
ROOT = os.path.join(HOME, "Library", "Application Support", "ScreenshotVault")
INBOX = os.path.join(ROOT, "inbox")
ARCHIVE = os.path.join(ROOT, "archive")
THUMBS = os.path.join(ROOT, "thumbs")
CONFIG = os.path.join(ROOT, "config.json")
BIN = os.path.dirname(os.path.abspath(__file__))
PORT = 7333

IMAGE_EXT = {".png", ".jpg", ".jpeg", ".heic", ".tiff", ".tif", ".gif"}
VIDEO_EXT = {".mov", ".mp4"}
MEDIA_EXT = IMAGE_EXT | VIDEO_EXT | {".pdf"}

# "Screenshot 2026-09-15 at 3.36.22 PM" / "Screen Recording 2026-09-15 at 15.36.22"
MAC_NAME = re.compile(
    r"(\d{4}-\d{2}-\d{2}) at (\d{1,2})\.(\d{2})\.(\d{2})[\s ]*([AP]M)?", re.I
)

state = {"version": 0}
lock = threading.Lock()


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def load_config():
    try:
        with open(CONFIG) as f:
            return json.load(f)
    except Exception:
        return {"retention_days": 0}


def save_config(cfg):
    with open(CONFIG, "w") as f:
        json.dump(cfg, f)


def bump():
    with lock:
        state["version"] += 1


# ---------- capture handling ----------

def front_app():
    try:
        asn = subprocess.run(["lsappinfo", "front"], capture_output=True, text=True, timeout=2).stdout.strip()
        out = subprocess.run(["lsappinfo", "info", "-only", "name", asn],
                             capture_output=True, text=True, timeout=2).stdout
        m = re.search(r'"LSDisplayName"="(.*)"', out)
        return m.group(1) if m else ""
    except Exception:
        return ""


def capture_time(path):
    m = MAC_NAME.search(os.path.basename(path))
    if m:
        day, h, mi, s, ampm = m.groups()
        h = int(h)
        if ampm:
            h = h % 12 + (12 if ampm.upper() == "PM" else 0)
        try:
            return datetime.strptime(f"{day} {h:02d}:{mi}:{s}", "%Y-%m-%d %H:%M:%S")
        except ValueError:
            pass
    st = os.stat(path)
    return datetime.fromtimestamp(getattr(st, "st_birthtime", st.st_mtime))


def clean_label(s):
    s = re.sub(r'[/:\\\x00-\x1f]', "-", s).strip(" .-")
    return s[:60]


def archive_path(when, label, ext):
    day_dir = os.path.join(ARCHIVE, when.strftime("%Y-%m"), when.strftime("%Y-%m-%d"))
    os.makedirs(day_dir, exist_ok=True)
    base = when.strftime("%Y-%m-%d at %H.%M.%S")
    if label:
        base += f" - {label}"
    dest = os.path.join(day_dir, base + ext)
    n = 2
    while os.path.exists(dest):
        dest = os.path.join(day_dir, f"{base} ({n}){ext}")
        n += 1
    return dest


def make_thumb(src):
    rel = os.path.relpath(src, ARCHIVE)
    ext = os.path.splitext(src)[1].lower()
    if ext not in IMAGE_EXT:
        return
    dst = os.path.join(THUMBS, rel) + ".jpg"
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    subprocess.run(["sips", "-Z", "640", "-s", "format", "jpeg", "-s", "formatOptions", "75",
                    src, "--out", dst], capture_output=True, timeout=30)


def copy_to_clipboard(path):
    ext = os.path.splitext(path)[1].lower()
    p = path.replace("\\", "\\\\").replace('"', '\\"')
    if ext == ".png":
        script = f'set the clipboard to (read (POSIX file "{p}") as «class PNGf»)'
    elif ext in (".jpg", ".jpeg"):
        script = f'set the clipboard to (read (POSIX file "{p}") as JPEG picture)'
    elif ext in (".tif", ".tiff"):
        script = f'set the clipboard to (read (POSIX file "{p}") as TIFF picture)'
    else:
        # videos / other: copy as a file so it pastes into Finder, Slack, Mail…
        script = f'set the clipboard to (POSIX file "{p}")'
    r = subprocess.run(["osascript", "-e", script], capture_output=True, text=True, timeout=10)
    return r.returncode == 0


def is_ready(path):
    """macOS writes captures as hidden temp files and renames them; wait for a stable size."""
    try:
        a = os.path.getsize(path)
        time.sleep(0.15)
        return a > 0 and a == os.path.getsize(path)
    except OSError:
        return False


def ingest(path, label, to_clipboard):
    ext = os.path.splitext(path)[1].lower()
    when = capture_time(path)
    dest = archive_path(when, clean_label(label), ext)
    shutil.move(path, dest)
    if to_clipboard:
        copy_to_clipboard(dest)
    make_thumb(dest)
    return dest


def watch_inbox():
    os.makedirs(INBOX, exist_ok=True)
    while True:
        try:
            names = [n for n in os.listdir(INBOX)
                     if not n.startswith(".") and os.path.splitext(n)[1].lower() in MEDIA_EXT]
            if names:
                app = front_app()
                paths = sorted((os.path.join(INBOX, n) for n in names), key=os.path.getmtime)
                for i, p in enumerate(paths):
                    if not is_ready(p):
                        continue
                    dest = ingest(p, app, to_clipboard=(i == len(paths) - 1))
                    log("filed", dest)
                bump()
        except Exception as e:
            log("watch error:", e)
        time.sleep(0.3)


def retention_loop():
    while True:
        try:
            days = int(load_config().get("retention_days", 0))
            if days > 0:
                cutoff = time.time() - days * 86400
                removed = [it["path"] for it in list_items() if it["ts"] < cutoff]
                if removed:
                    trash(removed)
                    log("retention removed", len(removed))
        except Exception as e:
            log("retention error:", e)
        time.sleep(3600)


# ---------- library ----------

def list_items():
    items = []
    for dirpath, _, files in os.walk(ARCHIVE):
        for n in files:
            ext = os.path.splitext(n)[1].lower()
            if n.startswith(".") or ext not in MEDIA_EXT:
                continue
            full = os.path.join(dirpath, n)
            rel = os.path.relpath(full, ARCHIVE)
            st = os.stat(full)
            m = re.match(r"(\d{4}-\d{2}-\d{2}) at (\d{2})\.(\d{2})\.(\d{2})(?: - (.*?))?(?: \(\d+\))?$",
                         os.path.splitext(n)[0])
            if m:
                ts = datetime.strptime(f"{m.group(1)} {m.group(2)}:{m.group(3)}:{m.group(4)}",
                                       "%Y-%m-%d %H:%M:%S").timestamp()
                app = m.group(5) or ""
            else:
                ts, app = st.st_mtime, ""
            kind = "image" if ext in IMAGE_EXT else "video" if ext in VIDEO_EXT else "file"
            thumb = os.path.exists(os.path.join(THUMBS, rel) + ".jpg")
            items.append({"path": rel, "name": n, "app": app, "ts": ts, "kind": kind,
                          "size": st.st_size, "thumb": thumb})
    items.sort(key=lambda x: x["ts"], reverse=True)
    return items


def safe(rel, base=ARCHIVE):
    full = os.path.realpath(os.path.join(base, rel))
    if not full.startswith(os.path.realpath(base) + os.sep):
        raise ValueError("bad path")
    return full


def trash(rels):
    tdir = os.path.join(HOME, ".Trash")
    for rel in rels:
        full = safe(rel)
        if not os.path.exists(full):
            continue
        name = os.path.basename(full)
        dest = os.path.join(tdir, name)
        if os.path.exists(dest):
            stem, ext = os.path.splitext(name)
            dest = os.path.join(tdir, f"{stem} {int(time.time() * 1000)}{ext}")
        shutil.move(full, dest)
        th = os.path.join(THUMBS, rel) + ".jpg"
        if os.path.exists(th):
            os.remove(th)
        d = os.path.dirname(full)
        while d != ARCHIVE and os.path.isdir(d) and not os.listdir(d):
            os.rmdir(d)
            d = os.path.dirname(d)
    bump()


# ---------- server ----------

CTYPES = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".gif": "image/gif",
          ".heic": "image/heic", ".tif": "image/tiff", ".tiff": "image/tiff", ".pdf": "application/pdf",
          ".mov": "video/quicktime", ".mp4": "video/mp4", ".html": "text/html; charset=utf-8"}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, body, ctype="application/json", extra=None):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store" if ctype.startswith(("application/json", "text/html")) else "max-age=3600")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, full):
        with open(full, "rb") as f:
            data = f.read()
        self.send(200, data, CTYPES.get(os.path.splitext(full)[1].lower(), "application/octet-stream"))

    def do_GET(self):
        url = urlparse(self.path)
        p = unquote(url.path)
        try:
            if p == "/":
                self.send_file(os.path.join(BIN, "gallery.html"))
            elif p == "/widget":
                self.send_file(os.path.join(BIN, "widget.html"))
            elif p == "/api/list":
                total = 0
                items = list_items()
                for it in items:
                    total += it["size"]
                self.send(200, {"items": items, "total": total, "version": state["version"],
                                "config": load_config()})
            elif p == "/api/version":
                self.send(200, {"version": state["version"]})
            elif p.startswith("/files/"):
                self.send_file(safe(p[len("/files/"):]))
            elif p.startswith("/thumbs/"):
                self.send_file(safe(p[len("/thumbs/"):], THUMBS))
            else:
                self.send(404, {"error": "not found"})
        except (ValueError, FileNotFoundError):
            self.send(404, {"error": "not found"})

    def do_POST(self):
        # Only accept requests from the gallery page itself.
        origin = self.headers.get("Origin", "")
        if origin and origin not in (f"http://localhost:{PORT}", f"http://127.0.0.1:{PORT}"):
            return self.send(403, {"error": "forbidden"})
        try:
            n = int(self.headers.get("Content-Length", 0))
            body = json.loads(self.rfile.read(n) or b"{}")
            p = urlparse(self.path).path
            if p == "/api/copy":
                ok = copy_to_clipboard(safe(body["path"]))
                self.send(200, {"ok": ok})
            elif p == "/api/openvault":
                subprocess.run(["open", f"http://localhost:{PORT}/"])
                self.send(200, {"ok": True})
            elif p == "/api/reveal":
                subprocess.run(["open", "-R", safe(body["path"])])
                self.send(200, {"ok": True})
            elif p == "/api/open":
                subprocess.run(["open", safe(body["path"])])
                self.send(200, {"ok": True})
            elif p == "/api/delete":
                trash(body["paths"])
                self.send(200, {"ok": True})
            elif p == "/api/config":
                cfg = load_config()
                cfg["retention_days"] = max(0, int(body.get("retention_days", 0)))
                save_config(cfg)
                self.send(200, {"ok": True, "config": cfg})
            else:
                self.send(404, {"error": "not found"})
        except Exception as e:
            self.send(400, {"error": str(e)})


def serve():
    for d in (INBOX, ARCHIVE, THUMBS):
        os.makedirs(d, exist_ok=True)
    threading.Thread(target=watch_inbox, daemon=True).start()
    threading.Thread(target=retention_loop, daemon=True).start()
    log(f"Screenshot Vault on http://localhost:{PORT}")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()


def import_files(paths):
    for p in paths:
        if os.path.splitext(p)[1].lower() not in MEDIA_EXT or not os.path.isfile(p):
            continue
        print("imported", ingest(p, "", to_clipboard=False))


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "serve":
        serve()
    elif len(sys.argv) >= 3 and sys.argv[1] == "import":
        for d in (ARCHIVE, THUMBS):
            os.makedirs(d, exist_ok=True)
        import_files(sys.argv[2:])
    else:
        print(__doc__)
