#!/usr/bin/env python3
"""
Tiny static server for review.html. Adds an /audio/<sample-id> route so the
page can play recordings from anywhere on disk without copying them in.

Run:
    python3 serve_review.py [--port 8765] [--run latest]
    open http://127.0.0.1:8765/review.html
"""

import argparse
import http.server
import json
import os
import socketserver
import sys
import webbrowser
from pathlib import Path
from urllib.parse import urlparse

BENCH_DIR = Path(__file__).parent
RESULTS_DIR = BENCH_DIR / "results"


def resolve_run(run_id: str) -> Path:
    if run_id == "latest":
        runs = sorted([p for p in RESULTS_DIR.iterdir() if p.is_dir()],
                      key=lambda p: p.stat().st_mtime, reverse=True)
        if not runs:
            sys.exit("No runs in results/")
        return runs[0]
    p = RESULTS_DIR / run_id
    if not p.is_dir():
        sys.exit(f"Run not found: {p}")
    return p


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--run", default="latest", help="run-id under results/ (default: latest)")
    ap.add_argument("--no-open", action="store_true")
    args = ap.parse_args()

    run_dir = resolve_run(args.run)
    scored = run_dir / "scored.json"
    if not scored.exists():
        sys.exit(f"scored.json missing at {scored} — run score_results.py {run_dir.name} first.")

    samples = json.loads((BENCH_DIR / "samples.json").read_text())
    path_by_id = {s["id"]: s["path"] for s in samples}

    os.chdir(BENCH_DIR)

    overrides_path = run_dir / "overrides.json"

    class Handler(http.server.SimpleHTTPRequestHandler):
        def do_GET(self):
            parsed = urlparse(self.path)
            if parsed.path.startswith("/audio/"):
                sid = parsed.path[len("/audio/"):]
                p = path_by_id.get(sid)
                if p is None or not Path(p).exists():
                    self.send_error(404, f"sample {sid} not found")
                    return
                self.send_response(200)
                self.send_header("Content-Type", "audio/wav")
                self.send_header("Content-Length", str(Path(p).stat().st_size))
                self.send_header("Accept-Ranges", "bytes")
                self.end_headers()
                with open(p, "rb") as f:
                    self.wfile.write(f.read())
                return
            if parsed.path == "/scored.json":
                data = scored.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                return
            if parsed.path == "/overrides.json":
                if overrides_path.exists():
                    data = overrides_path.read_bytes()
                else:
                    data = b"{}"
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                return
            return super().do_GET()

        def do_POST(self):
            parsed = urlparse(self.path)
            if parsed.path == "/overrides.json":
                length = int(self.headers.get("Content-Length", "0"))
                raw = self.rfile.read(length)
                try:
                    body = json.loads(raw)
                    assert isinstance(body, dict)
                    for k, v in body.items():
                        assert isinstance(k, str) and isinstance(v, str)
                except Exception as e:
                    self.send_error(400, f"bad body: {e}")
                    return
                overrides_path.write_text(json.dumps(body, indent=2) + "\n")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b'{"ok":true}')
                return
            self.send_error(404, "not found")

        def log_message(self, fmt, *args):  # quieter
            pass

    with socketserver.TCPServer(("127.0.0.1", args.port), Handler) as httpd:
        url = f"http://127.0.0.1:{args.port}/review.html"
        print(f"Serving {BENCH_DIR} on {url}  (run: {run_dir.name})")
        if not args.no_open:
            webbrowser.open(url)
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopping")
    return 0


if __name__ == "__main__":
    sys.exit(main())
