import json
import re
import matplotlib.pyplot as plt
import os
import numpy as np

file_path = './stellar-supercluster.log'
save_path = './'
exploratory_plots = False
target_num_workers = 192
result_path = "./ranges.dat"

# Function to read the last 10 lines from a file and extract the metrics
def extract_metrics_from_tail(file_path, max_buffer_size=1_000_000):
    with open(file_path, 'rb') as f:        
        f.seek(0,2)
        file_size = f.tell()
        offset = max(file_size - max_buffer_size, 0)
        f.seek(offset, 0)
        lines = f.readlines()

    # Find the last line containing "job monitor '/metrics'"
    for line in lines[::-1]:
        line_str = line.decode('utf-8')
        if "job monitor query 'http://ssc-job-monitor.services.stellar-ops.com/stellar-supercluster/metrics'" in line_str:
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
            'apply_time_s': float(apply_time_ms.replace('ms', '')) / 1e3,
            'total_time_s': float(total_time_s.replace('s', ''))
        })

    return sorted(parsed_metrics, key=lambda x: x['ledger_end'])

# Function to plot and save total_time_seconds w.r.t ledger_end
def plot_total_time_vs_ledger_end(parsed_metrics, save_path):
    plt.figure(figsize=(10, 6))
    ledger_end = [m['ledger_end'] for m in parsed_metrics]
    total_time_s = [m['total_time_s'] for m in parsed_metrics]
    # print(sum(total_time_s))
    plt.scatter(ledger_end, total_time_s, marker='x')
    plt.xlabel('Ledger End')
    plt.ylabel('Total Time (s)')
    plt.title('Total Time (s) vs Ledger End')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'total_time_vs_ledger_end.png'))

# Function to plot and save ledger_apply_time_seconds w.r.t ledger_end
def plot_ledger_apply_time_vs_ledger_end(parsed_metrics, save_path):
    plt.figure(figsize=(10, 6))
    ledger_end = [m['ledger_end'] for m in parsed_metrics]
    apply_time_s = [m['apply_time_s'] for m in parsed_metrics]
    # print(sum(apply_time_s))
    plt.scatter(ledger_end, apply_time_s, marker='x')
    plt.xlabel('Ledger End')
    plt.ylabel('Ledger Apply Time (s)')
    plt.title('Ledger Apply Time (s) vs Ledger End')
    plt.grid(True)
    plt.savefig(os.path.join(save_path, 'ledger_apply_time_vs_ledger_end.png'))

# Function to plot and save the ratio of apply_time_milliseconds/total_time_seconds w.r.t ledger_end
def plot_ratio_apply_time_vs_ledger_end(parsed_metrics, save_path):
    plt.figure(figsize=(10, 6))
    ledger_end = [m['ledger_end'] for m in parsed_metrics]
    ratio = [m['apply_time_s']/ m['total_time_s'] for m in parsed_metrics]
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
    metrics = extract_metrics_from_tail(file_path)
    parsed_metrics = parse_metrics(metrics)
    assert parsed_metrics
    if exploratory_plots:
        os.makedirs(save_path, exist_ok=True)
        plot_total_time_vs_ledger_end(parsed_metrics, save_path)
        plot_ledger_apply_time_vs_ledger_end(parsed_metrics, save_path)
        plot_ratio_apply_time_vs_ledger_end(parsed_metrics, save_path)
        plot_jobs_per_worker(parsed_metrics, save_path)
        plot_total_time_per_worker(parsed_metrics, save_path)
        plt.show()

    total_apply_time = sum([m['apply_time_s'] for m in parsed_metrics]) 
    target_apply_time = total_apply_time / target_num_workers
    print("total apply time (secs):", total_apply_time)
    print("target number of workers:", target_num_workers)
    print("target average ledger apply time per worker: ", target_apply_time)
    
    local_sum = 0
    check_points = []
    estimate_apply_time = []
    for i in range(len(parsed_metrics)):
        step = parsed_metrics[i]['apply_time_s']
        local_sum += step
        if local_sum > target_apply_time:
            prev_sum = local_sum - step
            if local_sum - target_apply_time > target_apply_time - prev_sum and i != 0:
                # the current step is too far, take the previous step
                check_points.append(parsed_metrics[i-1]['ledger_end'])
                estimate_apply_time.append(prev_sum)
                local_sum = step
            else:
                # take the current step
                check_points.append(parsed_metrics[i]['ledger_end'])
                estimate_apply_time.append(local_sum)
                local_sum = 0

            if len(check_points) == target_num_workers:
                break

    print("Check points: ", check_points)
    print("Estimate apply time: ", estimate_apply_time)
    mean = np.mean(estimate_apply_time)
    std_dev = np.std(estimate_apply_time, ddof=1)    
    print(f"Mean: {mean}, Standard Deviation: {std_dev}")
    # plt.figure(figsize=(10, 6))
    # plt.plot(estimate_apply_time, 'x')
    # plt.show()

    diff = [check_points[0]] + [check_points[i] - check_points[i-1] for i in range(1, len(check_points))]
    with open(result_path, "w") as file:
        file.write("end_range/ledgers_to_apply\n")
        for element, diff in zip(check_points, diff):
            file.write(f"{element}/{diff}\n")
    
    
main(file_path, save_path)
