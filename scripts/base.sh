#!/bin/sh

# Shared implementation behind the MinBlockTimeMixed wrapper scripts.
#
# This file is *sourced*, never executed:
#
#     . "$(dirname "$0")/base.sh"
#
# It defines only constants and functions, so sourcing it has no side effects.
# A wrapper is expected to:
#
#   1. Override any of the presets below (NETWORK_SIZE_LIMIT, PUBNET_DATA_FILE,
#      BLOCK_TIME_MS, ...).
#   2. Define its own usage(), which die() prints on argument errors.
#   3. Parse its own flags, delegating anything it does not recognize to
#      base_parse_arg.
#   4. Call base_validate_args, then base_resolve_derived, then
#      base_run_selected_loads.
#
# Benchmark setup:
# - One mission run is started for each selected Soroban load flag:
#   --sac, --oz, and/or --soroswap. Passing multiple flags runs them
#   sequentially, not as one combined Soroban workload.
# - Every run includes CLASSIC_TX_RATE pre-generated classic payment TPS to
#   match current network conditions. The flag value supplies only the Soroban
#   TPS for that run, so total TPS is CLASSIC_TX_RATE + selected Soroban TPS.
# - The mission uses MinBlockTimeMixed's MIXED_PREGEN_* overlay-only loadgen
#   mode, simulated pubnet network delay, with NETWORK_SIZE_LIMIT nodes.
# - Validators are configured with automatic quorum sets
#   (--enable-relaxed-auto-qset-config), which needs a stellar-core build that
#   supports SKIP_HIGH_CRITICAL_VALIDATOR_CHECKS_FOR_TESTING.
# - The block-time search range is intentionally narrow: the mission searches
#   [BLOCK_TIME_MS - BLOCK_TIME_BAND_MS, BLOCK_TIME_MS + BLOCK_TIME_BAND_MS],
#   and because the band matches the mission's binary-search threshold that
#   leaves exactly one candidate to evaluate: BLOCK_TIME_MS itself.
# - simulate-apply-duration is derived from SIMULATE_APPLY_BUDGET_MS and the
#   total TPS so the synthetic apply sleep budget remains roughly constant as
#   the requested Soroban rate changes. A wrapper that changes
#   SIMULATE_APPLY_BUDGET_MS moves the per-ledger budget the derivation aims
#   at; the per-operation value is always computed, never set directly.
#
# Result interpretation:
# - A zero exit and a "Minimum sustainable block time: ..." log line means the
#   run passed the mission SLA for the tested target: on every node,
#   ledger.age.closed-histogram P75 was within the configured band around T and
#   P99 was <= 2*T, and the network stayed synced/consistent.
# - Because the search range is narrow, interpret a successful result as "the
#   image passed this workload at the target close time", not as a precise
#   minimum block-time measurement.
# - A non-zero exit, "No block time ... satisfied the SLA", loadgen failure, or
#   sync/consistency failure means the image/setup did not pass this benchmark
#   configuration.
# - With more than one load flag selected, each load is an independent
#   benchmark: a failing run does not skip the ones after it. The failed loads
#   are listed once every run has finished, and the exit status is non-zero if
#   any of them failed.

IMAGE_REPOSITORY="746476062914.dkr.ecr.us-east-1.amazonaws.com/dev"

PROJECT="src/App/App.fsproj"
MISSION="MinBlockTimeMixed"
DESTINATION="evaluation"

NETDELAY_IMAGE="$IMAGE_REPOSITORY/sdf-netdelay:latest"
POSTGRES_IMAGE="$IMAGE_REPOSITORY/postgres:9.5.22"
NGINX_IMAGE="$IMAGE_REPOSITORY/nginx:latest"
PROMETHEUS_EXPORTER_IMAGE="$IMAGE_REPOSITORY/stellar-core-prometheus-exporter:latest"

INGRESS_INTERNAL_DOMAIN="stellar-supercluster.kube001-ssc-eks.services.stellar-ops.com"
AVOID_NODE_LABELS="purpose:ssc"

CLASSIC_TX_RATE=200
NUM_PREGENERATED_TXS=1000000
GENESIS_TEST_ACCOUNT_COUNT=1000000
SIMULATE_APPLY_WEIGHT=100

# Matches searchThresholdMs in src/FSLibrary/MinBlockTimeTest.fs. Searching
# [T - BLOCK_TIME_BAND_MS, T + BLOCK_TIME_BAND_MS] leaves the mission's binary
# search exactly one candidate to evaluate, T, rather than measuring a true
# minimum.
BLOCK_TIME_BAND_MS=100

# Presets a wrapper may override before it parses arguments, plus the values
# base_parse_arg fills in from the shared flags.
DATA_ROOT="$(pwd)"
STELLAR_CORE_IMAGE=
SAC_TX_RATE=
OZ_TX_RATE=
SOROSWAP_TX_RATE=
BLOCK_TIME_MS=5000
NETWORK_SIZE_LIMIT=277
PUBNET_DATA_FILE=

# Total milliseconds of synthetic apply sleep to aim for per ledger. Divided by
# the per-ledger operation count to get the per-operation microseconds the
# mission actually takes.
SIMULATE_APPLY_BUDGET_MS=600

# Cluster-external hostname the driver connects to. Empty means "let the
# mission fall back to the route hostname it derives from
# INGRESS_INTERNAL_DOMAIN".
INGRESS_EXTERNAL_HOST=

# Reports a usage error against the calling wrapper's usage(), and exits.
die() {
	printf '%s\n' "$1" >&2
	usage >&2
	exit 1
}

# Reports an error that the usage text would not help with, and exits.
fail() {
	printf '%s\n' "$1" >&2
	exit 1
}

is_nonnegative_integer() {
	case "$1" in
	"" | *[!0-9]*)
		return 1
		;;
	*)
		return 0
		;;
	esac
}

# Parses the shared option at "$1", if it is one. Call as
# `base_parse_arg "$@"`: a function cannot shift its caller's arguments, so
# BASE_PARSE_SHIFT reports how many were consumed instead. A shift count of 0
# means "$1" is not a shared option, and the caller must handle or reject it.
base_parse_arg() {
	BASE_PARSE_SHIFT=2

	case "$1" in
	--stellar-core-image | --image)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "$1 requires an image."
		fi
		STELLAR_CORE_IMAGE="$2"
		;;
	--stellar-core-image=* | --image=*)
		STELLAR_CORE_IMAGE="${1#*=}"
		BASE_PARSE_SHIFT=1
		;;
	--data-root | --supercluster-root)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "$1 requires a path."
		fi
		DATA_ROOT="$2"
		;;
	--data-root=* | --supercluster-root=*)
		DATA_ROOT="${1#*=}"
		BASE_PARSE_SHIFT=1
		;;
	--ingress-external-host)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "$1 requires a hostname."
		fi
		INGRESS_EXTERNAL_HOST="$2"
		;;
	--ingress-external-host=*)
		INGRESS_EXTERNAL_HOST="${1#*=}"
		# An empty value would silently fall back to the derived route
		# hostname, so reject it like the two-token form does.
		if [ -z "$INGRESS_EXTERNAL_HOST" ]; then
			die "--ingress-external-host requires a hostname."
		fi
		BASE_PARSE_SHIFT=1
		;;
	--sac)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "--sac requires a Soroban tx rate."
		fi
		SAC_TX_RATE="$2"
		;;
	--sac=*)
		SAC_TX_RATE="${1#*=}"
		BASE_PARSE_SHIFT=1
		;;
	--oz)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "--oz requires a Soroban tx rate."
		fi
		OZ_TX_RATE="$2"
		;;
	--oz=*)
		OZ_TX_RATE="${1#*=}"
		BASE_PARSE_SHIFT=1
		;;
	--soroswap)
		if [ "$#" -lt 2 ] || [ -z "$2" ]; then
			die "--soroswap requires a Soroban tx rate."
		fi
		SOROSWAP_TX_RATE="$2"
		;;
	--soroswap=*)
		SOROSWAP_TX_RATE="${1#*=}"
		BASE_PARSE_SHIFT=1
		;;
	-h | --help)
		usage
		exit 0
		;;
	*)
		BASE_PARSE_SHIFT=0
		;;
	esac
}

validate_tx_rate() {
	flag="${1:?usage: validate_tx_rate FLAG RATE}"
	tx_rate="${2:?usage: validate_tx_rate FLAG RATE}"

	if ! is_nonnegative_integer "$tx_rate"; then
		fail "$flag rate must be a non-negative integer."
	fi
}

base_validate_args() {
	if [ -z "$STELLAR_CORE_IMAGE" ]; then
		die "A Stellar Core image is required."
	fi

	if [ -z "$SAC_TX_RATE" ] && [ -z "$OZ_TX_RATE" ] && [ -z "$SOROSWAP_TX_RATE" ]; then
		die "At least one load flag is required: --sac, --oz, or --soroswap."
	fi

	if [ -n "$SAC_TX_RATE" ]; then
		validate_tx_rate "--sac" "$SAC_TX_RATE"
	fi

	if [ -n "$OZ_TX_RATE" ]; then
		validate_tx_rate "--oz" "$OZ_TX_RATE"
	fi

	if [ -n "$SOROSWAP_TX_RATE" ]; then
		validate_tx_rate "--soroswap" "$SOROSWAP_TX_RATE"
	fi

	# The band is subtracted from the block time to get the search floor, so a
	# block time at or below it would produce a non-positive lower bound.
	if ! is_nonnegative_integer "$BLOCK_TIME_MS" || [ "$BLOCK_TIME_MS" -le "$BLOCK_TIME_BAND_MS" ]; then
		fail "Block time must be an integer greater than ${BLOCK_TIME_BAND_MS}ms."
	fi

	if ! is_nonnegative_integer "$SIMULATE_APPLY_BUDGET_MS"; then
		fail "Simulated apply budget must be a non-negative integer."
	fi

	# Non-numeric values make "[" exit 2 rather than 1, so test for the
	# accepted range and negate: anything unparseable is rejected too.
	if ! [ "$NETWORK_SIZE_LIMIT" -ge 1 ] 2>/dev/null; then
		fail "Network size limit must be a positive integer."
	fi
}

# Fills in everything derived from values a wrapper or the command line may
# have changed, so this must run after argument parsing.
base_resolve_derived() {
	if [ -z "$PUBNET_DATA_FILE" ]; then
		fail "No pubnet data set is configured."
	fi

	PUBNET_DATA="$DATA_ROOT/data/$PUBNET_DATA_FILE"
	TIER1_KEYS="$DATA_ROOT/data/tier1keys.json"
	MIN_BLOCK_TIME_MS=$((BLOCK_TIME_MS - BLOCK_TIME_BAND_MS))
	MAX_BLOCK_TIME_MS=$((BLOCK_TIME_MS + BLOCK_TIME_BAND_MS))
}

calculate_simulate_apply_duration() {
	classic_tx_rate="${1:?usage: calculate_simulate_apply_duration CLASSIC_TX_RATE SOROBAN_TX_RATE}"
	soroban_tx_rate="${2:?usage: calculate_simulate_apply_duration CLASSIC_TX_RATE SOROBAN_TX_RATE}"

	if ! is_nonnegative_integer "$classic_tx_rate" || ! is_nonnegative_integer "$soroban_tx_rate"; then
		printf '%s\n' "Tx rates must be non-negative integers." >&2
		return 1
	fi

	total_tx_rate=$((classic_tx_rate + soroban_tx_rate))
	if [ "$total_tx_rate" -eq 0 ]; then
		printf '%s\n' "Total tx rate must be greater than zero." >&2
		return 1
	fi

	simulate_apply_duration=$((SIMULATE_APPLY_BUDGET_MS * 1000000 / (total_tx_rate * BLOCK_TIME_MS)))

	# The division truncates, so a budget that is small next to the per-ledger
	# operation count would quietly disable the synthetic apply sleep instead
	# of shortening it. A zero budget asks for exactly that, so reject only the
	# case where a budget that was asked for rounds away.
	if [ "$SIMULATE_APPLY_BUDGET_MS" -ne 0 ] && [ "$simulate_apply_duration" -eq 0 ]; then
		printf '%s\n' "An apply budget of ${SIMULATE_APPLY_BUDGET_MS}ms rounds down to 0us per operation at $total_tx_rate TPS and a ${BLOCK_TIME_MS}ms close time; raise the budget." >&2
		return 1
	fi

	printf '%s\n' "$simulate_apply_duration"
}

resolve_min_block_time_mixed_mode() {
	case "$1" in
	sac | mixed_pregen_sac_payment)
		printf '%s\n' "mixed_pregen_sac_payment"
		;;
	oz | mixed_pregen_oz_token_transfer)
		printf '%s\n' "mixed_pregen_oz_token_transfer"
		;;
	soroswap | mixed_pregen_soroswap_swap)
		printf '%s\n' "mixed_pregen_soroswap_swap"
		;;
	*)
		printf '%s\n' "Unsupported mode '$1'. Use one of: sac, oz, soroswap." >&2
		return 1
		;;
	esac
}

run_min_block_time_mixed() {
	mode_alias="${1:?usage: run_min_block_time_mixed MODE SOROBAN_TX_RATE [MISSION_ARG...]}"
	soroban_tx_rate="${2:?usage: run_min_block_time_mixed MODE SOROBAN_TX_RATE [MISSION_ARG...]}"
	shift 2
	# Both derivations report their own error, so fail the load rather than
	# handing dotnet an empty value.
	min_block_time_mixed_mode="$(resolve_min_block_time_mixed_mode "$mode_alias")" || return 1
	simulate_apply_duration="$(calculate_simulate_apply_duration "$CLASSIC_TX_RATE" "$soroban_tx_rate")" || return 1

	if [ -n "$INGRESS_EXTERNAL_HOST" ]; then
		set -- --ingress-external-host "$INGRESS_EXTERNAL_HOST" "$@"
	fi

	dotnet run \
		--project "$PROJECT" \
		mission "$MISSION" \
		--destination "$DESTINATION" \
		--image="$STELLAR_CORE_IMAGE" \
		--netdelay-image="$NETDELAY_IMAGE" \
		--postgres-image="$POSTGRES_IMAGE" \
		--nginx-image="$NGINX_IMAGE" \
		--prometheus-exporter-image="$PROMETHEUS_EXPORTER_IMAGE" \
		--ingress-internal-domain="$INGRESS_INTERNAL_DOMAIN" \
		--avoid-node-labels="$AVOID_NODE_LABELS" \
		--export-to-prometheus \
		--enable-relaxed-auto-qset-config \
		--classic-tx-rate="$CLASSIC_TX_RATE" \
		--soroban-tx-rate="$soroban_tx_rate" \
		--min-block-time-mixed-mode="$min_block_time_mixed_mode" \
		--min-block-time-ms="$MIN_BLOCK_TIME_MS" \
		--max-block-time-ms="$MAX_BLOCK_TIME_MS" \
		--num-pregenerated-txs="$NUM_PREGENERATED_TXS" \
		--genesis-test-account-count="$GENESIS_TEST_ACCOUNT_COUNT" \
		--simulate-apply-weight "$SIMULATE_APPLY_WEIGHT" \
		--simulate-apply-duration "$simulate_apply_duration" \
		--pubnet-data "$PUBNET_DATA" \
		--tier1-keys "$TIER1_KEYS" \
		--network-size-limit "$NETWORK_SIZE_LIMIT" \
		--require-node-labels=purpose:largetests \
		--tolerate-node-taints=largetests \
		"$@"
}

# Runs the mission for one load flag, if that flag was selected. Records the
# flag in BASE_FAILED_LOADS when the run fails, so that a failure neither
# aborts the loads that follow nor goes unreported.
base_run_one_load() {
	load_alias="$1"
	load_tx_rate="$2"
	shift 2

	if [ -z "$load_tx_rate" ]; then
		return 0
	fi

	run_min_block_time_mixed "$load_alias" "$load_tx_rate" "$@"
	load_status=$?

	if [ "$load_status" -ne 0 ]; then
		printf '%s\n' "Load --$load_alias exited with status $load_status." >&2
		BASE_FAILED_LOADS="$BASE_FAILED_LOADS --$load_alias"
	fi
}

# Runs one mission per selected load flag, appending any arguments given here
# to every mission command line. Returns non-zero if any of the runs failed.
base_run_selected_loads() {
	BASE_FAILED_LOADS=

	base_run_one_load sac "$SAC_TX_RATE" "$@"
	base_run_one_load oz "$OZ_TX_RATE" "$@"
	base_run_one_load soroswap "$SOROSWAP_TX_RATE" "$@"

	if [ -n "$BASE_FAILED_LOADS" ]; then
		printf '%s\n' "Failed loads:$BASE_FAILED_LOADS" >&2
		return 1
	fi

	return 0
}
