"""The monitor's HTTP surface.

Everything the mission driver needs, so it never reads cluster state to run the
mission: the profile goes in through /start, status comes out of /status, and
the logs are pulled per file. The alternative for the logs was `kubectl exec`,
which proxies every byte through the API server -- measured 0.3 MB per range, so
~1.2 GB of control-plane traffic on a 4000-range run, for bytes that have no
business there.

/healthz and /prometheus predate this and keep their consumers: the kubelet's
livenessProbe, and the `kubernetes-pods` scrape job that relabels
prometheus.io/path onto __metrics_path__ and so reaches the non-standard path.
"""

import json
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from prometheus_client import CONTENT_TYPE_LATEST, REGISTRY, generate_latest

import config
from logger import build_logger

logger = build_logger('http_server')

# Set by job_monitor before serve(). A tuple rather than an import, because
# job_monitor imports this module.
status_source = None            # () -> (dict, lock)
started = threading.Event()     # /start has delivered a profile
on_start = None                 # (doc) -> None, installs the profile

# One path element, no traversal, no dotfiles.
_SAFE_NAME = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._-]*$')


def _log_path(name):
    """An existing regular file in LOG_DIR named by `name`, or None."""
    if not _SAFE_NAME.match(name or ''):
        return None
    path = os.path.join(config.LOG_DIR, name)
    return path if os.path.isfile(path) else None


class RequestHandler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def _send(self, code, body=b'', ctype='application/json'):
        self.send_response(code)
        self.send_header('Content-type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self):
        if self.path == '/healthz':
            # Serving at all is the whole check: a process that answers here
            # still has its HTTP thread.
            self._send(200, b'ok', 'text/plain')
        elif self.path == '/prometheus':
            self._send(200, generate_latest(REGISTRY), CONTENT_TYPE_LATEST)
        elif self.path == '/status':
            snapshot, lock = status_source()
            with lock:
                body = json.dumps(snapshot, separators=(',', ':')).encode()
            self._send(200, body)
        elif self.path == '/logs':
            self._send(200, json.dumps(self._manifest(), separators=(',', ':')).encode())
        elif self.path.startswith('/logs/'):
            self._send_file(self.path[len('/logs/'):])
        else:
            self._send(404)

    def do_POST(self):
        if self.path != '/start':
            self._send(404)
            return
        try:
            raw = self.rfile.read(int(self.headers.get('Content-Length') or 0))
            doc = json.loads(raw) if raw else {}
        except ValueError as e:
            self._send(400, json.dumps({'error': f'invalid profile json: {e}'}).encode())
            return
        # Idempotent: a driver that retries after a timeout must not restart a
        # run that is already dispatching.
        if not started.is_set():
            try:
                on_start(doc)
            except ValueError as e:
                # A profile the run cannot proceed with. Answering 400 fails the
                # driver here, with the reason, rather than leaving it to poll a
                # monitor that will never dispatch.
                self._send(400, json.dumps({'error': str(e)}).encode())
                return
            started.set()
        self._send(200, b'{"started":true}')

    def _manifest(self):
        """Every artifact worth pulling, with the size and mtime a puller needs
        to tell "already have it" from "grew since last time".

        .state is excluded: it is the collector's resume cursor, one timestamp
        rewritten on every poll of a live range. It is meaningless once the pods
        are gone, and because it changes constantly a manifest diff would
        re-fetch one per in-flight range on every pass -- up to 1024 round trips
        for bytes that are garbage by the time the run ends.
        """
        out = []
        for name in os.listdir(config.LOG_DIR):
            path = os.path.join(config.LOG_DIR, name)
            if (_SAFE_NAME.match(name) and not name.endswith('.state')
                    and os.path.isfile(path)):
                st = os.stat(path)
                out.append({'name': name, 'size': st.st_size, 'mtime': int(st.st_mtime)})
        return out

    def _send_file(self, name):
        """One artifact, honouring Range so a cut transfer resumes instead of
        restarting. The collector appends to these while a pod runs, so the
        length is fixed once at open and never read past."""
        path = _log_path(name)
        if not path:
            self._send(404)
            return
        with open(path, 'rb') as fh:
            size = os.fstat(fh.fileno()).st_size
            start, end = 0, size - 1
            m = re.match(r'bytes=(\d+)-(\d*)', self.headers.get('Range') or '')
            partial = bool(m)
            if partial:
                start = int(m.group(1))
                end = int(m.group(2)) if m.group(2) else size - 1
                if start >= size:
                    self.send_response(416)
                    self.send_header('Content-Range', f'bytes */{size}')
                    self.end_headers()
                    return
            length = end - start + 1
            self.send_response(206 if partial else 200)
            self.send_header('Content-type', 'application/octet-stream')
            self.send_header('Content-Length', str(length))
            if partial:
                self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
            self.end_headers()
            fh.seek(start)
            remaining = length
            while remaining > 0:
                chunk = fh.read(min(1 << 20, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)

    def log_message(self, *args):
        pass  # the default handler logs every request to stderr


def serve(port=8080):
    # Threading, because a log pull is long-lived and must not block the
    # liveness probe or the driver's status poll behind it.
    logger.info('Starting httpd server on :%d', port)
    ThreadingHTTPServer(('', port), RequestHandler).serve_forever()
