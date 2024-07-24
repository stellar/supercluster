#!/bin/sh

floorSize=$LOGARITHMIC_FLOOR_LEDGERS
overlapLedgers=$OVERLAP_LEDGERS
startLedger=$(echo "$STARTING_LEDGER" | awk '{printf "%d", $1}')
latestLedgerNum=$(echo "$LATEST_LEDGER_NUM" | awk '{printf "%d", $1}')
numParallelism=$NUM_PARALLELISM

echo "starting logarithmic range generationg with the following inputs: 
floorSize=$floorSize
overlapLedgers=$overlapLedgers
startLedger=$startLedger
latestLedgerNum=$latestLedgerNum
numParallelism=$numParallelism"

generate_uniform () {
    sl="$1"
    el="$2"
    ss="$3"
    echo "generating uniform ranges from parameters: startLedger=$sl, endLedger=$el, segSize=$ss"
    while [ "$el" -gt "$sl" ]; do
        # clamp the segment size to the num of remaining ledgers to avoid doing redundant work
        ledgersPerJob=$((el - sl))
        if [ "$ledgersPerJob" -gt "$ss" ]; then
            ledgersPerJob=$ss
        fi
        ledgersToApply=$((ledgersPerJob + overlapLedgers));
        echo "${el}/${ledgersToApply}";
        # our queue assumes push-left-pop-right, but since we are generating the ranges in reverse order, here we push right
        redis-cli -h redis -p 6379 RPUSH ranges "${el}/${ledgersToApply}";
        el=$(( el - ledgersPerJob ));
        # sleep for a short duration to avoid overloading the redis-cli connection
        sleep 1
    done
}

endLedger=$((latestLedgerNum / 2))
chunkSize=$(( (endLedger - startLedger + 1) / numParallelism ))
while [ "$chunkSize" -gt "$floorSize" ]; do
    generate_uniform "$startLedger" "$endLedger" "$chunkSize"
    startLedger=$(( endLedger + 1 ))
    chunkSize=$(( chunkSize / 2 ))
    endLedger=$((startLedger + (chunkSize * numParallelism)  ))
done

# treat the rest with one uniform-ranged chunk
generate_uniform "$((endLedger+1))" "$latestLedgerNum" "$floorSize"
