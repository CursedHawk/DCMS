#!/usr/bin/env python3
"""
Truncates a DCMS container's Docker json log file. Nothing else.

WHAT THIS HOLDS, STATED PLAINLY. This process has write access to
/var/lib/docker/containers, which is root-owned and close to root on the host. It exists
because the Docker API has no "clear this container's logs" call -- the logs are files, and
emptying one means truncating it. Isolating that capability in a container that does one thing
is better than giving it to platform-api, which is internet-facing through Caddy.

It is OFF BY DEFAULT (compose profile "logjanitor"). A deployment that does not want this
capability does not run it, and the console reports the feature as unavailable rather than
offering a button that fails.

WHAT LIMITS IT, in order of how much each is worth:

  1. It only ever calls truncate(0). It never opens a file for writing, never unlinks, never
     creates. A bug here empties a log; it cannot plant one.
  2. The container must belong to THIS compose project. The name is resolved to an id through
     the read-only Docker socket proxy, and the label com.docker.compose.project must match.
     A container id supplied directly is not accepted.
  3. The path is rebuilt from the resolved id and checked to be inside the containers root
     after resolution, so a crafted name cannot walk out of it.
  4. Bearer token, compared in constant time.

It listens only on the compose network; no host port is published.
"""

import hmac
import json
import os
import re
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CONTAINERS_ROOT = os.environ.get("CONTAINERS_ROOT", "/var/lib/docker/containers")
DOCKER_API = os.environ.get("DOCKER_API", "http://docker-socket-proxy-ro:2375")
PROJECT = os.environ.get("COMPOSE_PROJECT", "dcms")
SECRET = os.environ.get("LOG_JANITOR_SECRET", "")
PORT = int(os.environ.get("PORT", "8080"))

# Docker container ids are 64 hex characters. Anything else never becomes a path.
ID_RE = re.compile(r"^[0-9a-f]{64}$")


def resolve(name):
    """Container name -> (id, project). Read-only Docker API; raises on anything unexpected."""
    query = urllib.parse.quote(json.dumps({"name": [f"^/{re.escape(name)}$"]}))
    url = f"{DOCKER_API}/containers/json?all=1&filters={query}"
    with urllib.request.urlopen(url, timeout=10) as response:
        containers = json.load(response)

    for c in containers:
        labels = c.get("Labels") or {}
        # Exact name match: the filter is a regex and Docker reports names with a leading slash.
        if name in [n.lstrip("/") for n in c.get("Names", [])]:
            return c.get("Id", ""), labels.get("com.docker.compose.project", "")
    return "", ""


def truncate(container_id):
    """Empty every json log segment for this container. Returns bytes freed."""
    directory = os.path.realpath(os.path.join(CONTAINERS_ROOT, container_id))
    root = os.path.realpath(CONTAINERS_ROOT)

    # After realpath, not before: a symlink inside the tree is the case this catches.
    if not directory.startswith(root + os.sep):
        raise PermissionError("resolved outside the containers root")

    freed = 0
    for entry in os.listdir(directory):
        # `<id>-json.log`, plus the rotated `<id>-json.log.1` .. `.N` the max-file setting makes.
        if not entry.startswith(f"{container_id}-json.log"):
            continue
        path = os.path.join(directory, entry)
        if not os.path.isfile(path) or os.path.islink(path):
            continue
        size = os.path.getsize(path)
        # truncate(), never open("w"): this can only ever shorten a file that already exists.
        os.truncate(path, 0)
        freed += size
    return freed


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        print("log-janitor: " + (fmt % args), flush=True)

    def _send(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"status": "healthy", "enabled": bool(SECRET)})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/truncate":
            self._send(404, {"error": "not found"})
            return

        # Fails closed. An unset secret means the sidecar is running misconfigured, and the
        # safe reading of that is "refuse everything", not "allow everything".
        if not SECRET:
            self._send(503, {"error": "LOG_JANITOR_SECRET is not set; refusing every request"})
            return

        supplied = (self.headers.get("Authorization") or "").removeprefix("Bearer ").strip()
        if not hmac.compare_digest(supplied, SECRET):
            self._send(401, {"error": "unauthorized"})
            return

        # BaseHTTPRequestHandler does not decode chunked bodies: it would read nothing and
        # report an empty request, which reads as a bug in the caller's serialiser rather than
        # a transfer encoding this server never supported. Say so instead.
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            self._send(411, {"error": "send a body with Content-Length; chunked is not supported"})
            return

        try:
            length = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(length) or b"{}") or {}
        except Exception:
            self._send(400, {"error": "expected {\"container\": \"<name>\"}"})
            return

        # Accepts either casing. The caller is a .NET service whose serialiser settings are not
        # this file's business, and a 400 that turns out to be a capital C is a bad afternoon.
        name = payload.get("container") or payload.get("Container") or ""
        if not name:
            self._send(400, {
                "error": "expected {\"container\": \"<name>\"}",
                "keysReceived": sorted(payload.keys()),
            })
            return

        # Rejects a path fragment before it can ever become one. An error that quotes what it
        # actually received saves the caller guessing at whose serialiser renamed the field.
        if not isinstance(name, str) or not name or "/" in name or ".." in name:
            self._send(400, {"error": "container must be a plain container name", "received": repr(name)})
            return

        try:
            container_id, project = resolve(name)
        except Exception as exc:
            self._send(502, {"error": f"could not reach the Docker API: {exc}"})
            return

        if not container_id or not ID_RE.match(container_id):
            self._send(404, {"error": f"no container named {name}"})
            return

        # The project check is the one that matters: it is what stops this from being a
        # general-purpose truncate-any-file-on-the-host service.
        if project != PROJECT:
            self._send(403, {"error": f"{name} does not belong to the {PROJECT} compose project"})
            return

        try:
            freed = truncate(container_id)
        except FileNotFoundError:
            self._send(404, {"error": "no log files for that container"})
            return
        except PermissionError as exc:
            self._send(403, {"error": str(exc)})
            return

        self.log_message("truncated %s (%s), freed %d bytes", name, container_id[:12], freed)
        self._send(200, {"container": name, "freedBytes": freed})


if __name__ == "__main__":
    print(f"log-janitor: listening on :{PORT}, project={PROJECT}, secret={'set' if SECRET else 'UNSET'}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
