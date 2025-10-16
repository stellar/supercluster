import os
import redis
import requests
import json
import sys
import logging
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from prometheus_client import Gauge, Counter, Histogram, generate_latest, REGISTRY, CONTENT_TYPE_LATEST
from datetime import datetime, timezone

# Configuration

# Histogram buckets
#                  5m  15m   30m    1h  1.5h    2h
metric_buckets = (300, 900, 1800, 3600, 5400, 7200, float("inf"))
REDIS_HOST = os.getenv('REDIS_HOST', 'redis')
REDIS_PORT = int(os.getenv('REDIS_PORT', '6379'))
JOB_QUEUE = os.getenv('JOB_QUEUE', 'ranges')
SUCCESS_QUEUE = os.getenv('SUCCESS_QUEUE', 'succeeded')
FAILED_QUEUE = os.getenv('FAILED_QUEUE', 'failed')
PROGRESS_QUEUE = os.getenv('PROGRESS_QUEUE', 'in_progress')
METRICS = os.getenv('METRICS', 'metrics')
WORKER_PREFIX = os.getenv('WORKER_PREFIX', 'stellar-core')
NAMESPACE = os.getenv('NAMESPACE', 'default')
WORKER_COUNT = int(os.getenv('WORKER_COUNT', 3))
LOGGING_INTERVAL_SECONDS = int(os.getenv('LOGGING_INTERVAL_SECONDS', 10))

def get_logging_level():
    name_to_level = {
        'CRITICAL': logging.CRITICAL,
        'ERROR': logging.ERROR,
        'WARNING': logging.WARNING,
        'INFO': logging.INFO,
        'DEBUG': logging.DEBUG,
    }
    result = name_to_level.get(os.getenv('LOGGING_LEVEL', 'INFO'))
    if result is not None:
        return result
    else:
        return logging.INFO

# Initialize Redis client
redis_client = redis.Redis(host=REDIS_HOST, port=REDIS_PORT, decode_responses=True)

# Configure logging
log_file_name = f"job_monitor_{datetime.now(timezone.utc).strftime('%Y-%m-%d_%H-%M-%S')}.log"
log_file_path = os.path.join('/data', log_file_name)
logging.basicConfig(level=get_logging_level(), format='%(asctime)s - %(levelname)s - %(message)s', handlers=[
    logging.StreamHandler(sys.stdout),
    logging.FileHandler(log_file_path),
])
logger = logging.getLogger()

# In-memory status data structure and threading lock
status = {
    'num_remain': 1, # initialize the job remaining to non-zero to indicate something is running, just the status hasn't been updated yet
    'queue_remain_count': 0,
    'queue_succeeded_count': 0,
    'queue_failed_count': 0,
    'queue_in_progress_count': 0,
    'jobs_failed': [],
    'jobs_in_progress': [],
    'workers': [],
    'workers_up': 0,
    'workers_down': 0,
    'workers_refresh_duration': 0,
    'mission_duration': 0,
}
status_lock = threading.Lock()

metrics = {
    'metrics': []
}
metrics_lock = threading.Lock()

# Create metrics
metric_catchup_queues = Gauge('ssc_parallel_catchup_queues', 'Exposes size of each job queues', ["queue"])
metric_workers = Gauge('ssc_parallel_catchup_workers', 'Exposes catch up worker status', ["status"])
metric_refresh_duration = Gauge('ssc_parallel_catchup_workers_refresh_duration_seconds', 'Time it took to refresh status of all workers')
metric_full_duration = Histogram('ssc_parallel_catchup_job_full_duration_seconds', 'Exposes full job duration as histogram', buckets=metric_buckets)
metric_tx_apply_duration = Histogram('ssc_parallel_catchup_job_tx_apply_duration_seconds', 'Exposes job TX apply duration as histogram', buckets=metric_buckets)
metric_mission_duration = Gauge('ssc_parallel_catchup_mission_duration_seconds', 'Number of seconds since the mission started ')
metric_retries = Counter('ssc_parallel_catchup_job_retried_count', 'Number of jobs that were retried')


class RequestHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/status':
            self.send_response(200)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            with status_lock:
                self.wfile.write(json.dumps(status).encode())
        elif self.path == '/prometheus':
            self.send_response(200)
            self.send_header('Content-type', CONTENT_TYPE_LATEST)
            self.end_headers()
            self.wfile.write(generate_latest(REGISTRY))
        elif self.path == '/metrics':
            self.send_response(200)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            with metrics_lock:
                self.wfile.write(json.dumps(metrics).encode())
        else:
            self.send_response(404)
            self.end_headers()

def retry_jobs_in_progress():
    while redis_client.llen(PROGRESS_QUEUE) > 0:
        job = redis_client.lmove(PROGRESS_QUEUE, JOB_QUEUE, "RIGHT", "LEFT")
        metric_retries.inc()
        logger.info("moved job %s from %s to %s", job, PROGRESS_QUEUE, JOB_QUEUE)

def update_status_and_metrics():
    global status
    mission_start_time = time.time()
    while True:
        try:
            # Ping each worker status
            worker_statuses = []
            all_workers_down = True
            workers_up = 0
            workers_down = 0
            logger.info("Starting worker liveness check")
            workers_refresh_start_time = time.time()
            for i in range(WORKER_COUNT):
                worker_name = f"{WORKER_PREFIX}-{i}.{WORKER_PREFIX}.{NAMESPACE}.svc.cluster.local"
                try:
                    response = requests.get(f"http://{worker_name}:11626/info")
                    logger.debug("Worker %s is running, status code %d, response: %s", worker_name, response.status_code, response.json())
                    worker_statuses.append({'worker_id': i, 'status': 'running', 'info': response.json()['info']['status']})
                    workers_up += 1
                    all_workers_down = False
                except requests.exceptions.RequestException:
                    logger.debug("Worker %s is down", worker_name)
                    worker_statuses.append({'worker_id': i, 'status': 'down'})
                    workers_down += 1
            workers_refresh_duration = time.time() - workers_refresh_start_time
            logger.info("Finished workers liveness check")
            # Retry stuck jobs
            if all_workers_down and redis_client.llen(PROGRESS_QUEUE) > 0:
                logger.info("all workers are down but some jobs are stuck in progress")
                logger.info("moving them from %s to %s queue", PROGRESS_QUEUE, JOB_QUEUE)
                retry_jobs_in_progress()

            # Check the queue status
            # For remaining and successful jobs, we just print their count, do not care what they are and who owns it
            queue_remain_count = redis_client.llen(JOB_QUEUE)
            queue_succeeded_count = redis_client.llen(SUCCESS_QUEUE)
            # For failed and in-progress jobs, we retrieve their full content
            jobs_failed = redis_client.lrange(FAILED_QUEUE, 0, -1)
            jobs_in_progress = redis_client.lrange(PROGRESS_QUEUE, 0, -1)
            queue_failed_count = len(jobs_failed)
            queue_in_progress_count = len(jobs_in_progress)
            # Get run duration
            mission_duration = time.time() - mission_start_time

            # update the status
            with status_lock:
                status = {
                    'num_remain': queue_remain_count,  # Needed for backwards compatibility with the rest of the code
                    'queue_remain_count': queue_remain_count,
                    'queue_succeeded_count': queue_succeeded_count,
                    'queue_failed_count': queue_failed_count,
                    'queue_in_progress_count': queue_in_progress_count,
                    'jobs_failed': jobs_failed,
                    'jobs_in_progress': jobs_in_progress,
                    'workers': worker_statuses,
                    'workers_up': workers_up,
                    'workers_down': workers_down,
                    'workers_refresh_duration': workers_refresh_duration,
                    'mission_duration': mission_duration,
                }
            metric_catchup_queues.labels(queue="remain").set(queue_remain_count)
            metric_catchup_queues.labels(queue="succeeded").set(queue_succeeded_count)
            metric_catchup_queues.labels(queue="failed").set(queue_failed_count)
            metric_catchup_queues.labels(queue="in_progress").set(queue_in_progress_count)
            metric_workers.labels(status="up").set(workers_up)
            metric_workers.labels(status="down").set(workers_down)
            metric_refresh_duration.set(workers_refresh_duration)
            metric_mission_duration.set(mission_duration)
            logger.info("Status: %s", json.dumps(status))

            # update the metrics
            new_metrics = redis_client.spop(METRICS, 1000)
            if len(new_metrics) > 0:
                with metrics_lock:
                    metrics['metrics'].extend(new_metrics)
                for timing in new_metrics:
                    # Example: 36024000/8320|213|1.47073e+06ms|2965s
                    _, _, tx_apply, full_duration = timing.split('|')
                    metric_full_duration.observe(float(full_duration.rstrip('s')))
                    metric_tx_apply_duration.observe(float(tx_apply.rstrip('ms'))/1000)
            logger.info("Metrics: %s", json.dumps(metrics))

        except Exception as e:
            logger.error("Error while getting status: %s", str(e))

        time.sleep(LOGGING_INTERVAL_SECONDS)

def run(server_class=HTTPServer, handler_class=RequestHandler):
    server_address = ('', 8080)
    httpd = server_class(server_address, handler_class)
    logger.info('Starting httpd server...')
    httpd.serve_forever()

if __name__ == '__main__':
    log_thread = threading.Thread(target=update_status_and_metrics)
    log_thread.daemon = True
    log_thread.start()

    run()
