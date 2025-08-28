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

# Run iperf3 clients for each peer
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

    # Run bidirectional test
    iperf3 -c $PEER_DNS_NAME \
           -p $SEND_PORT \
           -t $DURATION \
           --bidir \
           -J > /results/result-$PEER_SHORT-bidir.json 2>&1

    echo "Completed test to $PEER_SHORT"
done

echo "All tests completed"
sleep infinity