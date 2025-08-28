#!/usr/bin/env python3
"""
Parse iperf3 benchmark results from Kubernetes pods and generate network performance report.

This script processes raw benchmark data collected from the P2P network infrastructure test
using coordinated bidirectional iperf3 tests.

Bidirectional Testing Architecture:
- Each node pair tests only once using iperf3's --bidir flag (simultaneous bidirectional)
- Test coordination uses hash-based distribution to determine which peer initiates test
- Only the initiating node runs the iperf3 client; the peer runs the server
- A single bidirectional test measures:
  - Forward direction: initiator → peer (client to server)
  - Reverse direction: peer → initiator (server to client)
- Both nodes' metrics are updated from this single test result

Input Format (via stdin):
- JSON object containing:
  - test_id: Unique identifier for this benchmark run
  - pods: Array of pod data, each containing:
    - name: Pod name
    - node_name: Stellar-core node name
    - logs: Pod logs (used to extract node name)
    - kubectl_output: Raw concatenated iperf3 JSON results from /results/*.json
  - topology: Map of node names to lists of peer FQDNs
  - network_delay_enabled: Boolean indicating if network delays are configured

iperf3 Bidirectional JSON Structure:
Each bidirectional test produces a JSON object with:
- "start": Test configuration and start time
- "intervals": Per-second measurements for both directions
- "end": Summary statistics including:
  - "sum_sent": Forward direction bytes sent (initiator → peer)
  - "sum_received": Forward direction bytes received at peer
  - "sum_sent_bidir_reverse": Reverse direction bytes sent (peer → initiator)
  - "sum_received_bidir_reverse": Reverse direction bytes received at initiator
  - "streams": Array with per-stream statistics including RTT latency

Processing Steps:
1. Determine which peer would have initiated (hash-based distribution)
2. Parse each pod's bidirectional iperf3 JSON results
3. Aggregate metrics properly:
   - Forward traffic: Add to initiator's send and peer's receive
   - Reverse traffic: Add to peer's send and initiator's receive
   - RTT measurements: Share between both nodes (bidirectional measurement)
4. Calculate aggregate statistics:
   - Per-node totals and per-peer averages
   - Network-wide RTT statistics
   - Validate network balance (total send ≈ total receive)
5. Generate formatted report and save JSON results

Output:
- Formatted text report to stdout (displayed in logs)
- JSON results file saved to destination/{test_id}.json
- Network balance warnings if imbalance between total send and total receive exceeds 5%
"""

import json
import sys
import re
import statistics
from datetime import datetime, timezone
from typing import Dict, List, Optional


def parse_iperf3_json(json_str: str) -> Dict:
    """Parse a single iperf3 JSON result from bidirectional test.

    iperf3 bidirectional test output contains:
    - sum_sent: Data sent in the forward direction (client to server)
    - sum_received: Data received in the forward direction (at server)
    - sum_sent_bidir_reverse: Data sent in reverse direction (server to client)
    - sum_received_bidir_reverse: Data received in reverse (at client)
    """
    result = {
        'send_mbps': 0.0,
        'recv_mbps': 0.0,
        'retransmits': 0,
        'min_rtt_ms': None,
        'mean_rtt_ms': None,
        'max_rtt_ms': None,
        'reverse_send_mbps': 0.0,
        'reverse_recv_mbps': 0.0,
        'error': None
    }

    try:
        data = json.loads(json_str)

        if 'error' in data and data['error']:
            result['error'] = data['error']
            return result

        # Extract data from the 'end' section
        if 'end' in data:
            end = data['end']

            # Forward direction throughput
            if 'sum_sent' in end and end['sum_sent']:
                if 'bits_per_second' in end['sum_sent']:
                    result['send_mbps'] = end['sum_sent']['bits_per_second'] / 1_000_000
                if 'retransmits' in end['sum_sent']:
                    result['retransmits'] = end['sum_sent']['retransmits']

            if 'sum_received' in end and end['sum_received']:
                if 'bits_per_second' in end['sum_received']:
                    result['recv_mbps'] = end['sum_received']['bits_per_second'] / 1_000_000

            # Reverse direction throughput
            if 'sum_sent_bidir_reverse' in end and end['sum_sent_bidir_reverse']:
                if 'bits_per_second' in end['sum_sent_bidir_reverse']:
                    result['reverse_send_mbps'] = end['sum_sent_bidir_reverse']['bits_per_second'] / 1_000_000

            if 'sum_received_bidir_reverse' in end and end['sum_received_bidir_reverse']:
                if 'bits_per_second' in end['sum_received_bidir_reverse']:
                    result['reverse_recv_mbps'] = end['sum_received_bidir_reverse']['bits_per_second'] / 1_000_000

            # Extract RTT statistics from streams
            result.update(extract_rtt_from_streams(end.get('streams', [])))

    except json.JSONDecodeError as e:
        result['error'] = f"JSON parse error: {e}"
    except Exception as e:
        result['error'] = f"Unexpected error: {e}"

    return result


def extract_rtt_from_streams(streams: List[Dict]) -> Dict:
    rtt_stats = {'min_rtt_ms': None, 'mean_rtt_ms': None, 'max_rtt_ms': None}

    rtts = {'min': [], 'mean': [], 'max': []}
    for stream in streams:
        if 'sender' in stream and stream['sender']:
            sender = stream['sender']
            if 'min_rtt' in sender and sender['min_rtt'] is not None:
                rtts['min'].append(sender['min_rtt'] / 1000.0)
            if 'mean_rtt' in sender and sender['mean_rtt'] is not None:
                rtts['mean'].append(sender['mean_rtt'] / 1000.0)
            if 'max_rtt' in sender and sender['max_rtt'] is not None:
                rtts['max'].append(sender['max_rtt'] / 1000.0)

    # Aggregate RTT stats: min of mins, mean of means, max of maxs
    if rtts['min']:
        rtt_stats['min_rtt_ms'] = min(rtts['min'])
    if rtts['mean']:
        rtt_stats['mean_rtt_ms'] = statistics.mean(rtts['mean'])
    if rtts['max']:
        rtt_stats['max_rtt_ms'] = max(rtts['max'])

    return rtt_stats


def should_node_initiate_test(node1: str, node2: str) -> bool:
    """Determine which node should initiate the test using hash-based distribution.

    This provides better load balancing so we have a roughly equal number of servers per node.
    """
    # Calculate hash values for both nodes
    node1_hash = sum(ord(c) for c in node1)
    node2_hash = sum(ord(c) for c in node2)
    combined = node1_hash ^ node2_hash

    # Use XOR to determine initiator for balanced distribution
    if combined % 2 == 0:
        # Even: lower hash initiates
        should_initiate = node1_hash < node2_hash
    else:
        # Odd: higher hash initiates
        should_initiate = node1_hash > node2_hash

    # Fallback to lexicographic if hashes are equal since we're just summing string chars
    if node1_hash == node2_hash:
        should_initiate = node1 < node2

    return should_initiate


def aggregate_bidirectional_results(all_pods_data: List[Dict], topology: Dict[str, List[str]]) -> Dict[str, Dict]:
    """Aggregate bidirectional test results.

    When node A runs a bidirectional test to node B:
    - Forward (A→B): Counts as A's send and B's receive
    - Reverse (B→A): Counts as B's send and A's receive
    """
    # Initialize metrics for all nodes
    node_metrics = {}
    for pod_data in all_pods_data:
        node_name = pod_data.get('node_name', pod_data.get('name', 'unknown'))
        if node_name not in node_metrics:
            node_metrics[node_name] = {
                'total_send_mbps': 0.0,
                'total_recv_mbps': 0.0,
                'peer_count': 0,
                'min_rtt_ms': None,
                'mean_rtt_ms': [],
                'max_rtt_ms': None,
                'total_retransmits': 0,
                'connection_failures': 0
            }

    # Process test results from each pod
    for pod_data in all_pods_data:
        initiator_node = pod_data.get('node_name', pod_data.get('name', 'unknown'))
        kubectl_output = pod_data.get('kubectl_output', '')

        if not kubectl_output or '"end"' not in kubectl_output:
            continue

        # Get peers for this node and determine which tests it would initiate
        node_peers = topology.get(initiator_node, [])
        tested_peers = []

        for peer_fqdn in node_peers:
            peer_node = peer_fqdn.split('.')[0] if '.' in peer_fqdn else peer_fqdn
            if should_node_initiate_test(initiator_node, peer_node):
                tested_peers.append(peer_fqdn)

        # Parse all test results from this node's output
        parts = kubectl_output.replace('}\n{', '}###SPLIT###{').split('###SPLIT###')
        test_results = []

        for part in parts:
            if '"end"' in part and '"start"' in part:
                result = parse_iperf3_json(part)
                if not result['error']:
                    test_results.append(result)

        # Match results to peers and aggregate metrics
        for i, result in enumerate(test_results):
            if i < len(tested_peers):
                peer_fqdn = tested_peers[i]
                peer_node = peer_fqdn.split('.')[0] if '.' in peer_fqdn else peer_fqdn

                # Forward direction: initiator → peer
                node_metrics[initiator_node]['total_send_mbps'] += result['send_mbps']
                if peer_node in node_metrics:
                    node_metrics[peer_node]['total_recv_mbps'] += result['send_mbps']

                # Reverse direction: peer → initiator
                reverse_throughput = result.get('reverse_recv_mbps', 0) or result.get('reverse_send_mbps', 0)
                if peer_node in node_metrics and reverse_throughput > 0:
                    node_metrics[peer_node]['total_send_mbps'] += reverse_throughput
                    node_metrics[initiator_node]['total_recv_mbps'] += reverse_throughput

                # RTT measurements (bidirectional, so both nodes get the same values)
                update_rtt_metrics(node_metrics[initiator_node], result)
                if peer_node in node_metrics:
                    update_rtt_metrics(node_metrics[peer_node], result)

                # Retransmits are counted only for the initiator
                node_metrics[initiator_node]['total_retransmits'] += result['retransmits']

    # Finalize metrics and calculate aggregates
    finalize_node_metrics(node_metrics, topology)
    validate_network_balance(node_metrics)

    return node_metrics


def update_rtt_metrics(node_metrics: Dict, result: Dict):
    """Update RTT metrics for a node based on test results."""
    if result['mean_rtt_ms'] is not None:
        node_metrics['mean_rtt_ms'].append(result['mean_rtt_ms'])

    if result['min_rtt_ms'] is not None:
        if node_metrics['min_rtt_ms'] is None:
            node_metrics['min_rtt_ms'] = result['min_rtt_ms']
        else:
            node_metrics['min_rtt_ms'] = min(node_metrics['min_rtt_ms'], result['min_rtt_ms'])

    if result['max_rtt_ms'] is not None:
        if node_metrics['max_rtt_ms'] is None:
            node_metrics['max_rtt_ms'] = result['max_rtt_ms']
        else:
            node_metrics['max_rtt_ms'] = max(node_metrics['max_rtt_ms'], result['max_rtt_ms'])


def finalize_node_metrics(node_metrics: Dict[str, Dict], topology: Dict[str, List[str]]):
    """Finalize node metrics by calculating aggregates and setting peer counts."""
    for node_name, node in node_metrics.items():
        # Set actual peer count from topology
        if node['total_send_mbps'] > 0 or node['total_recv_mbps'] > 0:
            node['peer_count'] = len(topology.get(node_name, []))

        # Calculate mean RTT from collected samples
        if node['mean_rtt_ms']:
            rtts = node['mean_rtt_ms']
            node['mean_rtt_ms'] = statistics.mean(rtts) if rtts else None
        else:
            node['mean_rtt_ms'] = None


def validate_network_balance(node_metrics: Dict[str, Dict]):
    """Validate that total network send approximately equals total receive."""
    total_network_send = sum(node['total_send_mbps'] for node in node_metrics.values())
    total_network_recv = sum(node['total_recv_mbps'] for node in node_metrics.values())

    if total_network_send > 0:
        imbalance_percent = abs(total_network_send - total_network_recv) / total_network_send * 100
        if imbalance_percent > 5.0:  # Alert if more than 5% imbalance
            print(f"WARNING: Network imbalance detected! Total send: {total_network_send:.1f} Mbps, "
                  f"Total recv: {total_network_recv:.1f} Mbps ({imbalance_percent:.2f}% imbalance)", 
                  file=sys.stderr)
            print("This may indicate a bug in the benchmark coordination or severe packet loss.", 
                  file=sys.stderr)


def format_benchmark_results(results: List[Dict], test_id: str, network_delay_enabled: bool, duration_seconds: int = None) -> str:
    """Format benchmark results for display."""
    # Calculate aggregate metrics
    total_nodes = len(results)
    total_connections = sum(r['peer_count'] for r in results)
    avg_peers = statistics.mean([r['peer_count'] for r in results]) if results else 0

    # Filter valid results, we use -1 as the placeholder for no data
    valid_results = [r for r in results if r['total_send_mbps'] > 0 or r['total_recv_mbps'] > 0]

    # Calculate performance averages
    if valid_results:
        avg_send_total = statistics.mean([r['total_send_mbps'] for r in valid_results])
        avg_recv_total = statistics.mean([r['total_recv_mbps'] for r in valid_results])

        # Per-peer averages
        per_peer_sends = [r['total_send_mbps'] / r['peer_count'] 
                          for r in valid_results if r['peer_count'] > 0]
        per_peer_recvs = [r['total_recv_mbps'] / r['peer_count'] 
                          for r in valid_results if r['peer_count'] > 0]

        avg_send_per_peer = statistics.mean(per_peer_sends) if per_peer_sends else 0
        avg_recv_per_peer = statistics.mean(per_peer_recvs) if per_peer_recvs else 0

        # RTT statistics
        min_rtts = [r['min_rtt_ms'] for r in valid_results if r.get('min_rtt_ms') is not None]
        mean_rtts = [r['mean_rtt_ms'] for r in valid_results if r.get('mean_rtt_ms') is not None]
        max_rtts = [r['max_rtt_ms'] for r in valid_results if r.get('max_rtt_ms') is not None]

        overall_min_rtt = min(min_rtts) if min_rtts else 0
        overall_mean_rtt = statistics.mean(mean_rtts) if mean_rtts else 0
        overall_max_rtt = max(max_rtts) if max_rtts else 0
    else:
        avg_send_total = avg_recv_total = 0
        avg_send_per_peer = avg_recv_per_peer = 0
        overall_min_rtt = overall_mean_rtt = overall_max_rtt = 0

    # Format individual node results
    node_lines = []
    for r in results:
        if r['total_send_mbps'] < 0 and r['total_recv_mbps'] < 0:
            # No data for this node
            node_lines.append(f"  {r['node_name']}: N/A send, N/A recv, N/A RTT, N/A retransmits")
        else:
            # Format per-peer throughput info
            per_peer_info = ""
            if r['peer_count'] > 0 and (r['total_send_mbps'] > 0 or r['total_recv_mbps'] > 0):
                per_peer_send = r['total_send_mbps'] / r['peer_count']
                per_peer_recv = r['total_recv_mbps'] / r['peer_count']
                per_peer_info = f" ({per_peer_send:.1f}/{per_peer_recv:.1f} Mbps per peer)"

            # Format RTT info
            rtt_info = (f"{r['mean_rtt_ms']:.2f} ms mean RTT" 
                       if r.get('mean_rtt_ms') is not None else "N/A RTT")

            node_lines.append(
                f"  {r['node_name']}: {r['total_send_mbps']:.1f} Mbps send, "
                f"{r['total_recv_mbps']:.1f} Mbps recv{per_peer_info}, "
                f"{rtt_info}, "
                f"{r['total_retransmits']} retransmits"
            )

    total_failures = sum(r['connection_failures'] for r in results)
    timestamp = datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')

    return f"""
===============================================
Network Infrastructure Benchmark Results
===============================================
Test ID: {test_id}
Timestamp: {timestamp}

Infrastructure Configuration:
- Nodes: {total_nodes}
- Average peers per node: {avg_peers:.1f}
- Total connections: {total_connections}
- Test duration: {duration_seconds if duration_seconds else 'N/A'} seconds
- Network delays: {'enabled' if network_delay_enabled else 'disabled'}

Aggregate Performance Metrics:
- Connection failures: {total_failures}
- Average total throughput per node: {avg_send_total:.1f} Mbps send, {avg_recv_total:.1f} Mbps recv
- Average per-peer throughput: {avg_send_per_peer:.1f} Mbps send, {avg_recv_per_peer:.1f} Mbps recv

RTT Latency Statistics (across all nodes):
- Minimum: {overall_min_rtt:.2f} ms
- Mean: {overall_mean_rtt:.2f} ms
- Maximum: {overall_max_rtt:.2f} ms

Individual Node Results:
{chr(10).join(node_lines)}
===============================================
"""


def main():
    # Read input JSON from stdin
    try:
        input_json = sys.stdin.read()
        input_data = json.loads(input_json)
    except json.JSONDecodeError as e:
        print(f"Failed to parse input JSON: {e}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"Failed to read input: {e}", file=sys.stderr)
        sys.exit(1)

    # Extract node names from pod logs
    for pod_data in input_data['pods']:
        logs = pod_data.get('logs', '')
        node_match = re.search(r'Node (\S+) running', logs)
        if node_match:
            pod_data['node_name'] = node_match.group(1)
        else:
            pod_data['node_name'] = pod_data['name']

    # Aggregate test results
    node_metrics = aggregate_bidirectional_results(
        input_data['pods'],
        input_data['topology']
    )

    # Convert to results format
    results = []
    for node_name, metrics in node_metrics.items():
        results.append({
            'node_name': node_name,
            'peer_count': metrics['peer_count'],
            'total_send_mbps': metrics['total_send_mbps'] if metrics['total_send_mbps'] > 0 else -1,
            'total_recv_mbps': metrics['total_recv_mbps'] if metrics['total_recv_mbps'] > 0 else -1,
            'total_retransmits': metrics['total_retransmits'],
            'min_rtt_ms': metrics['min_rtt_ms'],
            'mean_rtt_ms': metrics['mean_rtt_ms'],
            'max_rtt_ms': metrics['max_rtt_ms'],
            'connection_failures': metrics['connection_failures']
        })

    # Format and print results
    formatted = format_benchmark_results(
        results,
        input_data['test_id'],
        input_data.get('network_delay_enabled', False),
        input_data.get('duration_seconds')
    )

    print(formatted)

    # Also output JSON for saving to destination folder
    output = {
        'test_id': input_data['test_id'],
        'results': results,
        'formatted_output': formatted
    }

    # Write JSON to a file if destination is provided
    import os
    dest_file = os.path.join('destination', f"{input_data['test_id']}.json")
    os.makedirs('destination', exist_ok=True)
    with open(dest_file, 'w') as f:
        json.dump(output, f, indent=2)

    # Print destination file path to stderr so F# can log it
    print(f"RESULTS_FILE: {dest_file}", file=sys.stderr)


if __name__ == '__main__':
    main()