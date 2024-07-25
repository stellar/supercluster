#!/bin/sh

# Check if required environment variables are set
if [ -z "$LEDGERS_PER_JOB" ]; then echo "LEDGERS_PER_JOB not set"; exit 1; fi
if [ -z "$OVERLAP_LEDGERS" ]; then echo "OVERLAP_LEDGERS not set"; exit 1; fi
if [ -z "$STARTING_LEDGER" ]; then echo "STARTING_LEDGER not set"; exit 1; fi
if [ -z "$LATEST_LEDGER_NUM" ]; then echo "LATEST_LEDGER_NUM not set"; exit 1; fi
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi

ledgersPerJob=$LEDGERS_PER_JOB
overlapLedgers=$OVERLAP_LEDGERS
startingLedger=$(echo "$STARTING_LEDGER" | awk '{printf "%d", $1}')
endRange=$(echo "$LATEST_LEDGER_NUM" | awk '{printf "%d", $1}')

echo "Generating uniform ledger ranges from parameters 
ledgersPerJob=$ledgersPerJob,
overlapLedgers=$overlapLedgers,
startingLedger=$startingLedger,
endRange=$endRange"

while [ "$endRange" -gt "$startingLedger" ]; do
    ledgersToApply=$((ledgersPerJob + overlapLedgers));
    echo "${endRange}/${ledgersToApply}";
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" RPUSH ranges "${endRange}/${ledgersToApply}";
    endRange=$(( endRange - ledgersPerJob ));
    # sleep for a short duration to avoid overloading the redis-cli connection
    sleep 1
done