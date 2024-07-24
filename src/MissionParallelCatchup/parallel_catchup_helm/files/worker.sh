#!/bin/sh

SLEEP_INTERVAL=10
LOG_DIR="/data"

# ensure redis-cli is available
if [ ! "$(redis-cli --version)" ]; then
    echo "redis-cli not found, please ensure running with a supported stellar-core version"
    exit 1
fi

while true; do
# Fetch the next job key from the Redis queue. 
# The queue operation is always push left pop right. 
JOB_KEY=$(redis-cli -h $REDIS_HOST -p $REDIS_PORT LMOVE "$JOB_QUEUE" "$PROGRESS_QUEUE" RIGHT LEFT)


if [ -n "$JOB_KEY" ]; then
    # Start timer
    START_TIME=$(date +%s)  
    echo "Processing job: $JOB_KEY"

    if [ ! "$(/usr/bin/stellar-core --conf /config/stellar-core.cfg new-db &&
    /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$JOB_KEY" --metric 'ledger.transaction.apply')" ]; then
    echo "Error processing job: $JOB_KEY"
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LPUSH "$FAILED_QUEUE" "$JOB_KEY"
    else
    echo "Successfully processed job: $JOB_KEY"
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LPUSH "$SUCCESS_QUEUE" "$JOB_KEY"
    fi
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LREM "$PROGRESS_QUEUE" -1 "$JOB_KEY"

    # Parse and extract the metrics from the log file
    LOG_FILE=$(ls -t $LOG_DIR/stellar-core-*.log | head -n 1)
    if [ -z "$LOG_FILE" ]; then
    echo "No log file found."
    exit 1
    fi

    transaction_sum=$(tac "$LOG_FILE" | grep -m 1 -B 11 "metric 'ledger.transaction.apply':" | grep "sum =" | awk '{print $NF}')
    echo "Log file: $LOG_FILE"
    echo "ledger.transaction.apply sum: $transaction_sum"

    # End timer and duration
    END_TIME=$(date +%s)
    DURATION=$((END_TIME - START_TIME))
    echo "Finish processing job: $JOB_KEY, duration (seconds): $DURATION"

    # publish metrics
    cat <<EOF | curl --data-binary @- http://pushgateway.services.stellar-ops.com:9091/metrics/job/${RELEASE_NAME}/instance/${POD_NAME}
total_duration_seconds{catchup_to_ledger="${JOB_KEY%/*}",ledgers_to_apply="${JOB_KEY#*/}"} $DURATION
ledger_transaction_apply_ms{catchup_to_ledger="${JOB_KEY%/*}",ledgers_to_apply="${JOB_KEY#*/}"} $(echo "$transaction_sum" | sed 's/ms$//')
EOF

else
    echo "No more jobs in the queue. Sleeping for $SLEEP_INTERVAL seconds..."
    sleep $SLEEP_INTERVAL
fi
done