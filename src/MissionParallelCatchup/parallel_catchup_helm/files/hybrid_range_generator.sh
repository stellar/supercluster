#!/bin/sh
if [ -z "$LATEST_LEDGER_NUM" ]; then echo "LATEST_LEDGER_NUM not set"; exit 1; fi
if [ -z "$OVERLAP_LEDGERS" ]; then echo "OVERLAP_LEDGERS not set"; exit 1; fi
if [ -z "$LEDGERS_PER_JOB" ]; then echo "LEDGERS_PER_JOB not set"; exit 1; fi
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi

file="ranges.dat"
endRange=0
ledgersToApply=0

tail -n +2 "$file" | while IFS='/' read -r endRange ledgersToApply; do
    ledgersToApply=$((ledgersToApply + OVERLAP_LEDGERS))
    echo "${endRange}/${ledgersToApply}";
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" RPUSH ranges "${endRange}/${ledgersToApply}";
    sleep 1
done

line=$(tail -n1 ranges.dat)
endRange=${line%/*}

if [ "$endRange" -lt "$LATEST_LEDGER_NUM" ]; then
    export LATEST_LEDGER_NUM="$LATEST_LEDGER_NUM"
    export OVERLAP_LEDGERS="$OVERLAP_LEDGERS"
    export STARTING_LEDGER="$endRange"
    export LEDGERS_PER_JOB="$LEDGERS_PER_JOB"
    export REDIS_HOST="$REDIS_HOST"
    export REDIS_PORT="$REDIS_PORT"

    chmod +x ./uniform_range_generator.sh
    ./uniform_range_generator.sh
fi