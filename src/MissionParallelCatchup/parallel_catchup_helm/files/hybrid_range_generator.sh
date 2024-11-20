#!/bin/sh
if [ -z "$LATEST_LEDGER_NUM" ]; then echo "LATEST_LEDGER_NUM not set"; exit 1; fi
if [ -z "$OVERLAP_LEDGERS" ]; then echo "OVERLAP_LEDGERS not set"; exit 1; fi
if [ -z "$LEDGERS_PER_JOB" ]; then echo "LEDGERS_PER_JOB not set"; exit 1; fi
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi

file="/scripts/ranges.dat"
endRange=0
ledgersToApply=0

# we first read pre-computed ranges from file, top-to-bottom with lower ledger
# ranges on top. 
tail -n +2 "$file" | while IFS='/' read -r endRange ledgersToApply; do
    ledgersToApply=$((ledgersToApply + OVERLAP_LEDGERS))
    echo "${endRange}/${ledgersToApply}";
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" RPUSH ranges "${endRange}/${ledgersToApply}";
    sleep 1
done
# after this point, we have a queue of pre-computed ledger ranges that are
# hopefully optimized for even compute time distribution.
# lowest ranges are on the left

line=$(tail -n1 $file)
endRange=${line%/*}

# for the remaining ledgers, we generate them uniformly.the
# `uniform_range_generator` script generates the remaining ledgers in reverse
# order, thus the higher ledger ranges will end up on the left
if [ "$endRange" -lt "$LATEST_LEDGER_NUM" ]; then
    export LATEST_LEDGER_NUM="$LATEST_LEDGER_NUM"
    export OVERLAP_LEDGERS="$OVERLAP_LEDGERS"
    export STARTING_LEDGER="$endRange"
    export LEDGERS_PER_JOB="$LEDGERS_PER_JOB"
    export REDIS_HOST="$REDIS_HOST"
    export REDIS_PORT="$REDIS_PORT"

    /bin/sh /scripts/uniform_range_generator.sh
fi