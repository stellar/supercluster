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
ledgersToApply=$((ledgersPerJob + overlapLedgers));

echo "$(date) Generating uniform ledger ranges from parameters
ledgersPerJob=$ledgersPerJob,
overlapLedgers=$overlapLedgers,
ledgersToApply=$ledgersToApply,
startingLedger=$startingLedger,
endRange=$endRange"

# Store redis commands in a file for bulk upload in a transaction
CMD_FILE=redis_bulk_load
echo "MULTI">$CMD_FILE  # Start redis transaction
while [ "$endRange" -gt "$startingLedger" ]; do
    echo "${endRange}/${ledgersToApply}";
    echo "RPUSH ranges \"${endRange}/${ledgersToApply}\"">>$CMD_FILE
    endRange=$(( endRange - ledgersPerJob ));
done
echo "EXEC">>$CMD_FILE  # Close redis transaction

echo "$(date) Created file $CMD_FILE with $(wc -l $CMD_FILE) lines. Loading into redis"
for i in $(seq 1 6);do
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" <$CMD_FILE
    if [ $? -eq 0 ]; then
        break
    else
        echo "$(date) Error inserting data. Sleeping and retrying"
        sleep 5
    fi
done

echo "$(date) Finished generating ranges"
