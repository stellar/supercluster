#!/bin/sh

# Check if required environment variables are set
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi
if [ -z "$JOB_QUEUE" ]; then echo "JOB_QUEUE not set"; exit 1; fi
if [ -z "$PROGRESS_QUEUE" ]; then echo "PROGRESS_QUEUE not set"; exit 1; fi
if [ -z "$FAILED_QUEUE" ]; then echo "FAILED_QUEUE not set"; exit 1; fi
if [ -z "$SUCCESS_QUEUE" ]; then echo "SUCCESS_QUEUE not set"; exit 1; fi
if [ -z "$METRICS" ]; then echo "METRICS not set"; exit 1; fi
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
JOB_KEY=$(redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" LMOVE "$JOB_QUEUE" "$PROGRESS_QUEUE" RIGHT LEFT)

if [ -n "$JOB_KEY" ]; then
    # Start timer
    START_TIME=$(date +%s)  
    echo "Processing job: $JOB_KEY"

    if [ ! "$(/usr/bin/stellar-core --conf /config/stellar-core.cfg new-db&&
    /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$JOB_KEY" --metric 'ledger.transaction.apply')" ]; then
    echo "Error processing job: $JOB_KEY"
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" LPUSH "$FAILED_QUEUE" "$JOB_KEY|$POD_NAME" # enhance the entry with pod name for tracking
    else
    echo "Successfully processed job: $JOB_KEY"
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" LPUSH "$SUCCESS_QUEUE" "$JOB_KEY"
    fi
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" LREM "$PROGRESS_QUEUE" -1 "$JOB_KEY"

    # Parse and extract the metrics from the log file
    LOG_FILE=$(ls -t $LOG_DIR/stellar-core*.log | head -n 1)
    if [ -z "$LOG_FILE" ]; then
    echo "No log file found."
    exit 1
    fi

    tx_apply_ms=$(tac "$LOG_FILE" | grep -m 1 -B 11 "metric 'ledger.transaction.apply':" | grep "sum =" | awk '{print $NF}')
    echo "Log file: $LOG_FILE"
    echo "ledger.transaction.apply sum: $tx_apply_ms"

    # End timer and duration
    END_TIME=$(date +%s)
    DURATION=$((END_TIME - START_TIME))s
    echo "Finish processing job: $JOB_KEY, duration: $DURATION"

    # push metrics
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" SADD "$METRICS" "$JOB_KEY|$tx_apply_ms|$DURATION"
else
    echo "No more jobs in the queue. Sleeping for $SLEEP_INTERVAL seconds..."
    sleep $SLEEP_INTERVAL
fi
done