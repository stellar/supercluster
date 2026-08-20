#!/bin/sh

# SLP transaction end-to-end latency measurement wrapper.
#
# Usage: sh scripts/measure_e2e.sh [SLP_EVAL_OPTION...] [-- MISSION_ARG...]
#
# A preset over scripts/slp_eval.sh that sets the options needed to measure
# tx end-to-end latency.

SLP_EVAL="$(dirname "$0")/slp_eval.sh"

NETWORK_SIZE_LIMIT=1000
PUBNET_DATA_FILE="public-network-data-2026-06-03-trimmed-located.json"
LOADGEN_KEYS_FILE="public-network-data-2026-06-03-loadgenkeys.json"

# Sets DATA_ROOT and SEPARATOR_GIVEN (whether the user supplied "--" in the
# arguments). Shifts its own copy of the arguments, leaving the caller's "$@"
# intact.
scan_args() {
	DATA_ROOT="$(pwd)"
	SEPARATOR_GIVEN=0

	while [ "$#" -gt 0 ]; do
		case "$1" in
		--)
			SEPARATOR_GIVEN=1
			return
			;;
		--data-root | --supercluster-root)
			DATA_ROOT="$2"
			;;
		--data-root=* | --supercluster-root=*)
			DATA_ROOT="${1#*=}"
			;;
		esac

		shift
	done
}

scan_args "$@"

# Add a separator if the caller didn't supply one
if [ "$SEPARATOR_GIVEN" -eq 0 ]; then
	set -- "$@" --
fi

exec sh "$SLP_EVAL" \
	--network-size-limit "$NETWORK_SIZE_LIMIT" \
	--pubnet-data "$DATA_ROOT/data/$PUBNET_DATA_FILE" \
	"$@" \
	--loadgen-keys "$DATA_ROOT/data/$LOADGEN_KEYS_FILE" \
	--measure-e2e-latency
