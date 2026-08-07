"""The monitor's HTTP surface: a liveness probe and the Prometheus scrape.

Two routes, both with a live consumer -- the kubelet's livenessProbe and the
`kubernetes-pods` scrape job, which relabels prometheus.io/path onto
__metrics_path__ and so reaches the non-standard /prometheus.
"""

from http.server import BaseHTTPRequestHandler, HTTPServer

from prometheus_client import CONTENT_TYPE_LATEST, REGISTRY, generate_latest

from logger import build_logger

logger = build_logger('http_server')


class RequestHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/healthz':
            # Serving at all is the whole check: a process that answers here
            # still has its HTTP thread.
            self.send_response(200)
            self.end_headers()
        elif self.path == '/prometheus':
            self.send_response(200)
            self.send_header('Content-type', CONTENT_TYPE_LATEST)
            self.end_headers()
            self.wfile.write(generate_latest(REGISTRY))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *args):
        pass  # the default handler logs every request to stderr


def serve(port=8080):
    logger.info('Starting httpd server on :%d', port)
    HTTPServer(('', port), RequestHandler).serve_forever()
