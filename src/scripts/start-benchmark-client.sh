#!/bin/bash
#
# Benchmark Client Pod Script
# ============================
# Starts iperf3 clients in a pod, one for each peer who is running an iperf3 server.
#
# Template Variables (replaced by F# before deployment):
#   NODE_PLACEHOLDER - The name of this node
#   PEERS_PLACEHOLDER - Comma-separated list of peer short names
#   NAMESPACE_PLACEHOLDER - Kubernetes namespace
#   DURATION_PLACEHOLDER - Test duration in seconds
#   INDEX_PLACEHOLDER - Global node index for port selection
#   RUN_ID_PLACEHOLDER - Unique run identifier
#   HEADLESS_SERVICE_PLACEHOLDER - Name of the headless service (without namespace)

NODE='NODE_PLACEHOLDER'
PEERS='PEERS_PLACEHOLDER'
NAMESPACE='NAMESPACE_PLACEHOLDER'
DURATION=DURATION_PLACEHOLDER
INDEX=INDEX_PLACEHOLDER
RUN_ID='RUN_ID_PLACEHOLDER'
HEADLESS_SERVICE='HEADLESS_SERVICE_PLACEHOLDER'

echo "Node $NODE running benchmark to peers: $PEERS"
mkdir -p /results

# Wait for servers to be ready and DNS to propagate
echo "Waiting 10 seconds for all servers to start and DNS to propagate..."
sleep 10

# Parse peer list
IFS=',' read -ra PEER_ARRAY <<< "$PEERS"

# Run iperf3 clients for each peer in parallel
pids=()
for PEER_DNS in "${PEER_ARRAY[@]}"; do
    if [ -z "$PEER_DNS" ]; then continue; fi

    PEER_SHORT="$PEER_DNS"

    # Skip self-connections
    NODE_SHORT=$(echo "$NODE" | rev | cut -d'-' -f1-2 | rev)
    if [ "$PEER_SHORT" = "$NODE_SHORT" ]; then
        echo "Skipping self-connection to $PEER_SHORT"
        continue
    fi

    echo "Processing peer: $PEER_SHORT"

    # Unique port based on our global index
    SEND_PORT=$((5201 + INDEX))

    # Use StatefulSet DNS pattern
    PEER_DNS_NAME="$RUN_ID-benchmark-${PEER_SHORT}-0.$HEADLESS_SERVICE.$NAMESPACE.svc.cluster.local"

    echo "Testing to $PEER_DNS_NAME on port $SEND_PORT"

    # Run bidirectional test in background (1 stream per peer, matching actual simulation)
    # Add 15 second grace period to timeout to account for connection setup and completion
    # Limit bandwidth to 300Mbps per direction to realistically simulate stellar-core.
    (
        timeout $((DURATION + 15)) iperf3 -c $PEER_DNS_NAME \
               -p $SEND_PORT \
               -t $DURATION \
               -b 1000M \
               --bidir \
               -J > /results/result-$PEER_SHORT-bidir.json 2>&1
        iperf_exit_code=$?
        if [ $iperf_exit_code -eq 0 ]; then
            echo "Completed test to $PEER_SHORT successfully"
        elif [ $iperf_exit_code -eq 124 ]; then
            echo "ERROR: Test to $PEER_SHORT timed out (exceeded $((DURATION + 10))s)"
            exit 124
        else
            echo "ERROR: Test to $PEER_SHORT failed with code $iperf_exit_code"
            exit $iperf_exit_code
        fi
    ) &

    pids+=($!)
done

echo "Waiting for all ${#pids[@]} parallel tests to complete..."
exit_code=0
failed_tests=()
successful_tests=0

for i in "${!pids[@]}"; do
    pid="${pids[$i]}"

    if wait $pid; then
        successful_tests=$((successful_tests + 1))
        echo "✓ Test completed successfully (PID: $pid)"
    else
        test_exit_code=$?
        exit_code=1
        failed_tests+=("PID $pid failed with code $test_exit_code")
        echo "✗ Test failed (PID: $pid, exit code: $test_exit_code)"
    fi
done

echo "================================"
echo "Benchmark Summary:"
echo "  Total tests: ${#pids[@]}"
echo "  Successful: $successful_tests"
echo "  Failed: ${#failed_tests[@]}"
if [ ${#failed_tests[@]} -gt 0 ]; then
    echo "  Failed tests:"
    for failure in "${failed_tests[@]}"; do
        echo "    - $failure"
    done
fi
echo "================================"

# Write exit code to a file so the orchestrator can check it
echo "$exit_code" > /results/exit_code

if [ $exit_code -ne 0 ]; then
    echo "ERROR: Some iperf3 tests failed. Exit code: $exit_code"
    echo "Container will remain alive for result collection..."
else
    echo "All tests completed successfully"
fi

# Always keep container alive for result collection
echo "Keeping container alive for result collection..."
sleep infinity