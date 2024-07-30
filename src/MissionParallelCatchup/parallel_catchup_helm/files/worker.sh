#!/bin/sh

# Check if required environment variables are set
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi
if [ -z "$JOB_QUEUE" ]; then echo "JOB_QUEUE not set"; exit 1; fi
if [ -z "$PROGRESS_QUEUE" ]; then echo "PROGRESS_QUEUE not set"; exit 1; fi
if [ -z "$FAILED_QUEUE" ]; then echo "FAILED_QUEUE not set"; exit 1; fi
if [ -z "$SUCCESS_QUEUE" ]; then echo "SUCCESS_QUEUE not set"; exit 1; fi
if [ -z "$RELEASE_NAME" ]; then echo "RELEASE_NAME not set"; exit 1; fi
if [ -z "$POD_NAME" ]; then echo "POD_NAME not set"; exit 1; fi

# ensure redis-cli is available
if [ ! "$(redis-cli --version)" ]; then
    echo "redis-cli not found, please ensure running with a supported stellar-core version"
    exit 1
fi

SLEEP_INTERVAL=10
LOG_DIR="/data"

while true; do
# Fetch the next job key from the Redis queue. 
# The queue operation is always push left pop right. 
JOB_KEY=$(redis-cli -h $REDIS_HOST -p $REDIS_PORT LMOVE "$JOB_QUEUE" "$PROGRESS_QUEUE" RIGHT LEFT)

if [ -n "$JOB_KEY" ]; then
    # Start timer
    START_TIME=$(date +%s)  
    echo "Processing job: $JOB_KEY"

    if [ ! "$(/usr/bin/stellar-core --conf /config/stellar-core.cfg new-db --console&&
    /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$JOB_KEY" --metric 'ledger.transaction.apply' --console)" ]; then
    echo "Error processing job: $JOB_KEY"
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LPUSH "$FAILED_QUEUE" "$JOB_KEY|$POD_NAME" # enhance the entry with pod name for tracking
    else
    echo "Successfully processed job: $JOB_KEY"
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LPUSH "$SUCCESS_QUEUE" "$JOB_KEY"
    fi
    redis-cli -h $REDIS_HOST -p $REDIS_PORT LREM "$PROGRESS_QUEUE" -1 "$JOB_KEY"

    # Parse and extract the metrics from the log file
    LOG_FILE=$(ls -t $LOG_DIR/stellar-core*.log | head -n 1)
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