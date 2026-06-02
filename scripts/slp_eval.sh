#!/bin/sh

# SLP mixed-load evaluation wrapper.
#
# This script runs the MinBlockTimeMixed mission against a stellar-core image
# using the 2025-06-24 pubnet topology data and the fixed benchmark parameters
# below. It is intended to answer: "does this image sustain the selected mixed
# classic/Soroban load at the normal 5s ledger close target?"
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
# - The block-time search range is intentionally narrow:
#   [MIN_BLOCK_TIME_MS, MAX_BLOCK_TIME_MS] = [4900, 5100]. With the mission's
#   100ms binary-search threshold, this effectively evaluates the 5s target
#   to match the current network close time.
# - simulate-apply-duration is derived from SIMULATE_APPLY_BUDGET_MS and the
#   total TPS so the synthetic apply sleep budget remains roughly constant as
#   the requested Soroban rate changes.
#
# Result interpretation:
# - A zero exit and a "Minimum sustainable block time: ..." log line means the
#   run passed the mission SLA for the tested target: on every node,
#   ledger.age.closed-histogram P75 was within the configured band around T and
#   P99 was <= 2*T, and the network stayed synced/consistent.
# - Because this wrapper uses a narrow range around 5s, interpret a successful
#   result as "the image passed this workload at the normal 5s close time", not
#   as a precise minimum block-time measurement.
# - A non-zero exit, "No block time ... satisfied the SLA", loadgen failure, or
#   sync/consistency failure means the image/setup did not pass this benchmark
#   configuration.

DATA_ROOT="$(pwd)"
STELLAR_CORE_IMAGE=
SAC_TX_RATE=
OZ_TX_RATE=
SOROSWAP_TX_RATE=

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
MIN_BLOCK_TIME_MS=4900
MAX_BLOCK_TIME_MS=5100
BLOCK_TIME_MS=$(((MIN_BLOCK_TIME_MS + MAX_BLOCK_TIME_MS) / 2))
NUM_PREGENERATED_TXS=1000000
GENESIS_TEST_ACCOUNT_COUNT=1000000
SIMULATE_APPLY_WEIGHT=100
SIMULATE_APPLY_BUDGET_MS=600
NETWORK_SIZE_LIMIT=277

usage() {
	cat <<EOF
Usage: $0 --stellar-core-image IMAGE [--data-root PATH] [--sac RATE] [--oz RATE] [--soroswap RATE]

Runs one MinBlockTimeMixed mission per selected load flag against IMAGE.
Each run always generates ${CLASSIC_TX_RATE} classic payment TPS plus the
selected Soroban TPS. For example, "--sac 50" tests ${CLASSIC_TX_RATE} classic
TPS + 50 SAC Soroban TPS; "--sac 50 --oz 25" runs two separate benchmarks.

Options:
  --stellar-core-image, --image IMAGE   Stellar Core image to evaluate. Required.
  --data-root, --supercluster-root PATH Root containing data/. Defaults to pwd.
  --sac RATE                            Run SAC load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  --oz RATE                             Run OZ load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  --soroswap RATE                       Run Soroswap load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  -h, --help                            Show this help.

Benchmark constants:
  Classic TPS:        ${CLASSIC_TX_RATE}
  Target close time:  ${BLOCK_TIME_MS}ms, evaluated via [${MIN_BLOCK_TIME_MS}, ${MAX_BLOCK_TIME_MS}]
  Network size limit: ${NETWORK_SIZE_LIMIT}
  Data set:           data/public-network-data-2025-06-24.json

Results:
  PASS: command exits 0 and logs "Minimum sustainable block time: ...".
        With this wrapper, treat that as passing the workload at the normal
        5s target close time.
  FAIL: command exits non-zero, reports no satisfying block time, or reports
        loadgen/sync/consistency failures.
EOF
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

parse_args() {
	while [ "$#" -gt 0 ]; do
		case "$1" in
		--stellar-core-image | --image)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				printf '%s\n' "$1 requires an image." >&2
				usage >&2
				exit 1
			fi
			STELLAR_CORE_IMAGE="$2"
			shift 2
			;;
		--stellar-core-image=* | --image=*)
			STELLAR_CORE_IMAGE="${1#*=}"
			shift
			;;
		--data-root | --supercluster-root)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				printf '%s\n' "$1 requires a path." >&2
				usage >&2
				exit 1
			fi
			DATA_ROOT="$2"
			shift 2
			;;
		--data-root=* | --supercluster-root=*)
			DATA_ROOT="${1#*=}"
			shift
			;;
		--sac)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				printf '%s\n' "--sac requires a Soroban tx rate." >&2
				usage >&2
				exit 1
			fi
			SAC_TX_RATE="$2"
			shift 2
			;;
		--sac=*)
			SAC_TX_RATE="${1#*=}"
			shift
			;;
		--oz)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				printf '%s\n' "--oz requires a Soroban tx rate." >&2
				usage >&2
				exit 1
			fi
			OZ_TX_RATE="$2"
			shift 2
			;;
		--oz=*)
			OZ_TX_RATE="${1#*=}"
			shift
			;;
		--soroswap)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				printf '%s\n' "--soroswap requires a Soroban tx rate." >&2
				usage >&2
				exit 1
			fi
			SOROSWAP_TX_RATE="$2"
			shift 2
			;;
		--soroswap=*)
			SOROSWAP_TX_RATE="${1#*=}"
			shift
			;;
		-h | --help)
			usage
			exit 0
			;;
		*)
			printf 'Unknown argument: %s\n' "$1" >&2
			usage >&2
			exit 1
			;;
		esac
	done
}

validate_tx_rate() {
	flag="${1:?usage: validate_tx_rate FLAG RATE}"
	tx_rate="${2:?usage: validate_tx_rate FLAG RATE}"

	if ! is_nonnegative_integer "$tx_rate"; then
		printf '%s\n' "$flag rate must be a non-negative integer." >&2
		exit 1
	fi
}

validate_args() {
	if [ -z "$STELLAR_CORE_IMAGE" ]; then
		printf '%s\n' "A Stellar Core image is required." >&2
		usage >&2
		exit 1
	fi

	if [ -z "$SAC_TX_RATE" ] && [ -z "$OZ_TX_RATE" ] && [ -z "$SOROSWAP_TX_RATE" ]; then
		printf '%s\n' "At least one load flag is required: --sac, --oz, or --soroswap." >&2
		usage >&2
		exit 1
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

	printf '%s\n' "$((SIMULATE_APPLY_BUDGET_MS * 1000000 / (total_tx_rate * BLOCK_TIME_MS)))"
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
	mode_alias="${1:?usage: run_min_block_time_mixed MODE SOROBAN_TX_RATE}"
	soroban_tx_rate="${2:?usage: run_min_block_time_mixed MODE SOROBAN_TX_RATE}"
	min_block_time_mixed_mode="$(resolve_min_block_time_mixed_mode "$mode_alias")"
	simulate_apply_duration="$(calculate_simulate_apply_duration "$CLASSIC_TX_RATE" "$soroban_tx_rate")"

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
		--tolerate-node-taints=largetests
}

parse_args "$@"
validate_args

PUBNET_DATA="$DATA_ROOT/data/public-network-data-2025-06-24.json"
TIER1_KEYS="$DATA_ROOT/data/tier1keys.json"

if [ -n "$SAC_TX_RATE" ]; then
	run_min_block_time_mixed sac "$SAC_TX_RATE"
fi

if [ -n "$OZ_TX_RATE" ]; then
	run_min_block_time_mixed oz "$OZ_TX_RATE"
fi

if [ -n "$SOROSWAP_TX_RATE" ]; then
	run_min_block_time_mixed soroswap "$SOROSWAP_TX_RATE"
fi
