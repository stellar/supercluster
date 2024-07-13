#!/bin/sh

ledgersPerJob=$LEDGERS_PER_JOB
overlapLedgers=$OVERLAP_LEDGERS
startingLedger=$STARTING_LEDGER
endRange=$LATEST_LEDGER_NUM

echo "Generating uniform ledger ranges from parameters ledgersPerJob=$ledgersPerJob, overlapLedgers=$overlapLedgers, startingLedger=$startingLedger, endRange=$endRange"

while [ "$endRange" -gt "$startingLedger" ]; do
    ledgersToApply=$((ledgersPerJob + overlapLedgers));
    echo "${endRange}/${ledgersToApply}";
    redis-cli -h redis -p 6379 RPUSH ranges "${endRange}/${ledgersToApply}";
    endRange=$(( endRange - ledgersPerJob ));
done