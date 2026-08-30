#!/bin/sh

# Check if required environment variables are set
if [ -z "$REDIS_HOST" ]; then echo "REDIS_HOST not set"; exit 1; fi
if [ -z "$REDIS_PORT" ]; then echo "REDIS_PORT not set"; exit 1; fi
if [ -z "$JOB_QUEUE" ]; then echo "JOB_QUEUE not set"; exit 1; fi
if [ -z "$PROGRESS_QUEUE" ]; then echo "PROGRESS_QUEUE not set"; exit 1; fi
if [ -z "$FAILED_QUEUE" ]; then echo "FAILED_QUEUE not set"; exit 1; fi
if [ -z "$SUCCESS_QUEUE" ]; then echo "SUCCESS_QUEUE not set"; exit 1; fi
if [ -z "$METRICS" ]; then echo "METRICS not set"; exit 1; fi
if [ -z "$JOB_OWNERS" ]; then echo "JOB_OWNERS not set"; exit 1; fi
if [ -z "$WORKER_CURRENT" ]; then echo "WORKER_CURRENT not set"; exit 1; fi
if [ -z "$ATTEMPTS" ]; then echo "ATTEMPTS not set"; exit 1; fi
if [ -z "$MAX_ATTEMPTS" ]; then echo "MAX_ATTEMPTS not set"; exit 1; fi
if [ -z "$RELEASE_NAME" ]; then echo "RELEASE_NAME not set"; exit 1; fi
if [ -z "$POD_NAME" ]; then echo "POD_NAME not set"; exit 1; fi

# ensure redis-cli is available
if [ ! "$(redis-cli --version)" ]; then
    echo "redis-cli not found, please ensure running with a supported stellar-core version"
    exit 1
fi

SLEEP_INTERVAL=10
LOG_DIR="/data"

# Hand back a range that a previous incarnation of this pod was holding.
#
# If our container was OOMKilled or our pod was evicted mid-catchup, the range
# we had claimed is still sitting in PROGRESS_QUEUE with nobody working on it.
# Nothing else can tell that from a range that is progressing normally, so it
# used to stay there until every other range in the mission had finished and
# the job monitor saw the whole fleet idle -- which is how a single dead worker
# added hours to a run (stellar/supercluster#409). We are the one process that
# knows for certain we are not working on it, so we are the one that returns it.
#
# The LREM guard means we only re-enqueue a range we actually removed, so this
# is a no-op if the range was already recovered some other way. Ranges that keep
# coming back here are failed rather than retried forever, otherwise a range
# that reliably OOMs would loop (stellar/supercluster#334).
#
# KEYS: progress queue, job queue, failed queue, attempts hash, worker current hash
# ARGV: job key, pod name, max attempts
# Returns: 0 if the range was no longer in progress, n > 0 if requeued as
#          attempt n, -1 if attempts were exhausted and the range was failed
RECLAIM_LUA='
redis.call("HDEL", KEYS[5], ARGV[2])
if redis.call("LREM", KEYS[1], -1, ARGV[1]) ~= 1 then return 0 end
local n = redis.call("HINCRBY", KEYS[4], ARGV[1], 1)
if n > tonumber(ARGV[3]) then
    redis.call("LPUSH", KEYS[3], ARGV[1] .. "|" .. ARGV[2])
    return -1
end
redis.call("LPUSH", KEYS[2], ARGV[1])
return n'

PREV_JOB=$(redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" HGET "$WORKER_CURRENT" "$POD_NAME")
if [ -n "$PREV_JOB" ]; then
    RECLAIMED=$(redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" EVAL "$RECLAIM_LUA" 5 \
        "$PROGRESS_QUEUE" "$JOB_QUEUE" "$FAILED_QUEUE" "$ATTEMPTS" "$WORKER_CURRENT" \
        "$PREV_JOB" "$POD_NAME" "$MAX_ATTEMPTS")
    case "$RECLAIMED" in
        0)  echo "Previously held range $PREV_JOB was already recovered elsewhere" ;;
        -1) echo "Range $PREV_JOB has now been orphaned more than $MAX_ATTEMPTS times; failing it" ;;
        *)  echo "Returned orphaned range $PREV_JOB to the front of $JOB_QUEUE as attempt $RECLAIMED" ;;
    esac
fi

while true; do
# Fetch the next job key from the Redis queue.
# Our ranges are generated in the order we want to run them from left to right, so we always pull from the left
JOB_KEY=$(redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" LMOVE "$JOB_QUEUE" "$PROGRESS_QUEUE" LEFT LEFT)
LMOVE_EXIT_CODE=$?

# Only process a job if the command succeeded AND we got a non-empty job key
if [ $LMOVE_EXIT_CODE -eq 0 ] && [ -n "$JOB_KEY" ]; then
    # Register ownership so the monitor knows which worker owns this job, and
    # record it against our pod name so that if we die and come back we can tell
    # which range we abandoned. Both directions are needed: JOB_OWNERS answers
    # "who has this range", WORKER_CURRENT answers "what was this pod holding".
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" <<EOF
MULTI
HSET "$JOB_OWNERS" "$JOB_KEY" "$POD_NAME"
HSET "$WORKER_CURRENT" "$POD_NAME" "$JOB_KEY"
EXEC
EOF
    if [ $? -ne 0 ]; then
        echo "Error: Failed to register job ownership for $JOB_KEY. Exiting."
        exit 1
    fi

    # Start timer
    START_TIME=$(date +%s)
    echo "Processing job: $JOB_KEY"

    # Run stellar-core: create new-db then catchup
    /usr/bin/stellar-core --conf /config/stellar-core.cfg new-db --console && \
    /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$JOB_KEY" \
        --metric 'ledger.transaction.apply' --console
    STELLAR_CORE_EXIT_CODE=$?

    # End timer and duration
    END_TIME=$(date +%s)
    DURATION=$((END_TIME - START_TIME))s
    echo "Finish processing job: $JOB_KEY, duration: $DURATION"

    # Check if both commands succeeded
    if [ $STELLAR_CORE_EXIT_CODE -eq 0 ]; then
        echo "Successfully processed job: $JOB_KEY"
        QUEUE_COMMAND="LPUSH $SUCCESS_QUEUE \"$JOB_KEY\""
    else
        echo "Error processing job: $JOB_KEY (exit code: $STELLAR_CORE_EXIT_CODE)"
        QUEUE_COMMAND="LPUSH $FAILED_QUEUE \"$JOB_KEY|$POD_NAME\""
    fi

    # Parse and extract the metrics from the log file
    LOG_FILE=$(ls -t "$LOG_DIR"/stellar-core*.log 2>/dev/null | head -n 1)
    if [ -z "$LOG_FILE" ]; then
        echo "No log file found in $LOG_DIR"
        exit 1
    fi

    tx_apply_ms=$(tac "$LOG_FILE" | grep -m 1 -B 11 "metric 'ledger.transaction.apply':" | grep "sum =" | awk '{print $NF}')
    echo "Log file: $LOG_FILE"
    echo "ledger.transaction.apply sum: $tx_apply_ms"
    # Validate metric was extracted successfully
    if [ -z "$tx_apply_ms" ]; then
        echo "Warning: Failed to extract metric 'ledger.transaction.apply' from log file"
        tx_apply_ms="N/A"
    fi

    # Push metrics to redis in a transaction to ensure data consistency. Retry for 5min on failures
    # Extract the pod ordinal (last hyphen-separated segment) from pod name like "release-name-stellar-core-0"
    core_id=$(echo "$POD_NAME" | awk -F'-' '{print $NF}')
    # Validate core_id was extracted successfully
    if [ -z "$core_id" ]; then
        echo "Error: Failed to extract core_id from POD_NAME: $POD_NAME"
        core_id="N/A"
    fi

    result=1  # Initialize to failure
    for i in $(seq 1 30);do
        redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" <<EOF
MULTI
$QUEUE_COMMAND
LREM "$PROGRESS_QUEUE" -1 "$JOB_KEY"
SADD "$METRICS" "$JOB_KEY|$core_id|$tx_apply_ms|$DURATION"
HDEL "$JOB_OWNERS" "$JOB_KEY"
HDEL "$WORKER_CURRENT" "$POD_NAME"
EXEC
EOF
        result=$?
        if [ $result -ne 0 ]; then
            echo "Redis transaction failed. Sleeping and retrying (attempt $i/30)"
            sleep 10
        else
            break
        fi
    done    
    # Check if all retries were exhausted
    if [ "$result" -ne 0 ]; then
        echo "Error: Redis transaction failed after all 30 retry attempts. Exiting."
        exit 1
    fi

else
    # Either Redis command failed OR queue is empty
    if [ $LMOVE_EXIT_CODE -ne 0 ]; then
        echo "Error: Failed to connect to Redis at $REDIS_HOST:$REDIS_PORT"
        echo "Exit code=$LMOVE_EXIT_CODE, Output: $JOB_KEY"
    else
        echo "$(date) No more jobs in the queue."
    fi
    echo "Sleeping for $SLEEP_INTERVAL seconds..."
    sleep $SLEEP_INTERVAL
fi
done
