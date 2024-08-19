import json
import re
import matplotlib.pyplot as plt
import os

# Function to read the last 10 lines from a file and extract the metrics
def extract_metrics(file_path):
    with open(file_path, 'rb') as file:
        file.seek(0, 2)  # Move to the end of the file
        file_size = file.tell()
        
        buffer_size = 1000000
        buffer = bytearray()
        lines = []
        
        while len(lines) < 10 and file_size > 0:
            file_size -= buffer_size
            file.seek(max(file_size, 0), 0)
            buffer.extend(file.read(min(buffer_size, file_size)))
            lines = buffer.splitlines()
        
        lines = lines[-10:]  # Last 10 lines
    
    # Find the line containing "job monitor '/metrics'"
    for line in lines:
        line_str = line.decode('utf-8')
        if "job monitor '/metrics'" in line_str:
            match = re.search(r':\s*(\{.*\})$', line_str)
            if match:
                metrics_data = json.loads(match.group(1))
                return metrics_data['metrics']
    
    return []

# Function to parse the metrics
def parse_metrics(metrics):
    parsed_metrics = []
    for metric in metrics:
        ledger_end, ledger_count_worker_info = metric.split('/')
        ledger_count, worker_id, apply_time_ms, total_time_s = ledger_count_worker_info.split('|')
        parsed_metrics.append({
            'ledger_end': int(ledger_end),
            'ledger_count': int(ledger_count),
            'worker_id': int(worker_id),
            'apply_time_ms': float(apply_time_ms.replace('ms', '')),
            'total_time_s': float(total_time_s.replace('s', ''))
        })
    return parsed_metrics

# Function to plot and save total_time_seconds w.r.t ledger_end
def plot_total_time_vs_ledger_end(parsed_metrics, save_path):
    plt.figure(figsize=(10, 6))
    ledger_end = [m['ledger_end'] for m in parsed_metrics]
    total_time_s = [m['total_time_s'] for m in parsed_metrics]
    plt.scatter(ledger_end, total_time_s, marker='x')
    plt.xlabel('Ledger End')
    plt.ylabel('Total Time (s)')
    plt.title('Total Time (s) vs Ledger End')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'total_time_vs_ledger_end.png'))

# Function to plot and save the ratio of apply_time_milliseconds/total_time_seconds w.r.t ledger_end
def plot_ratio_apply_time_vs_ledger_end(parsed_metrics, save_path):
    plt.figure(figsize=(10, 6))
    ledger_end = [m['ledger_end'] for m in parsed_metrics]
    ratio = [m['apply_time_ms'] / 1e3/ m['total_time_s'] for m in parsed_metrics]
    plt.scatter(ledger_end, ratio, marker='x')
    plt.xlabel('Ledger End')
    plt.ylabel('Apply Time / Total Time')
    plt.title('Apply Time Ratio vs Ledger End')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'ratio_apply_time_vs_ledger_end.png'))

# Function to plot and save the total number of jobs done by each worker
def plot_jobs_per_worker(parsed_metrics, save_path):
    worker_jobs = {}
    for m in parsed_metrics:
        worker_id = m['worker_id']
        if worker_id not in worker_jobs:
            worker_jobs[worker_id] = 0
        worker_jobs[worker_id] += 1
    
    worker_ids = list(worker_jobs.keys())
    job_counts = list(worker_jobs.values())
    
    plt.figure(figsize=(10, 6))
    plt.bar(worker_ids, job_counts)
    plt.xlabel('Worker ID')
    plt.ylabel('Total Jobs Done')
    plt.title('Total Jobs Done by Each Worker')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'jobs_per_worker.png'))

# Function to plot and save the total number of seconds done by each worker
def plot_total_time_per_worker(parsed_metrics, save_path):
    worker_time = {}
    for m in parsed_metrics:
        worker_id = m['worker_id']
        if worker_id not in worker_time:
            worker_time[worker_id] = 0.0
        worker_time[worker_id] += m['total_time_s']
    
    worker_ids = list(worker_time.keys())
    total_times = list(worker_time.values())
    
    plt.figure(figsize=(10, 6))
    plt.bar(worker_ids, total_times)
    plt.xlabel('Worker ID')
    plt.ylabel('Total Time (s)')
    plt.title('Total Time Done by Each Worker')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'total_time_per_worker.png'))

# Main function to execute the analysis and save plots
def main(file_path, save_path):
    metrics = extract_metrics(file_path)
    if metrics:
        parsed_metrics = parse_metrics(metrics)
        os.makedirs(save_path, exist_ok=True)
        plot_total_time_vs_ledger_end(parsed_metrics, save_path)
        plot_ratio_apply_time_vs_ledger_end(parsed_metrics, save_path)
        plot_jobs_per_worker(parsed_metrics, save_path)
        plot_total_time_per_worker(parsed_metrics, save_path)
        plt.show()
    else:
        print("No metrics found in the file.")

# Example usage
file_path = '../src/App/logs/stellar-supercluster.log'
save_path = '../src/App/logs'
main(file_path, save_path)
