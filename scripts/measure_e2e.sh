#!/bin/sh

# SLP transaction end-to-end latency measurement wrapper.
#
# This script runs the MinBlockTimeMixed mission with stellar-core's loadgen
# end-to-end latency metrics enabled, against the 2026-06-03 pubnet topology
# scaled out to 1000 nodes. It answers "how long does a transaction take to go
# from submission to application?" rather than "what is the minimum block time?"
#
# It differs from scripts/slp_eval.sh in that it uses a much larger network, a
# dedicated set of load-generating nodes, and exposes the knobs worth sweeping
# for a latency study: tier1 org count, ledger close time, and the synthetic
# apply budget.
#
# The benchmark setup and how to read the result are documented in
# scripts/base.sh, which holds the shared implementation.

. "$(dirname "$0")/base.sh"

NETWORK_SIZE_LIMIT=1000
PUBNET_DATA_FILE="public-network-data-2026-06-03-trimmed-located.json"
LOADGEN_KEYS_FILE="public-network-data-2026-06-03-loadgenkeys.json"

# Validators per tier1 org. Matches tier1OrgSize in
# src/FSLibrary/StellarNetworkData.fs, which is what --tier-1-orgs-to-add
# multiplies by.
TIER1_ORG_SIZE=3

TIER1_ORG_COUNT=

# usage() expands its heredoc at call time, and die() prints it after argument
# parsing, so the values it reports as defaults have to be captured before
# parse_args can overwrite them.
DEFAULT_BLOCK_TIME_MS="$BLOCK_TIME_MS"
DEFAULT_SIMULATE_APPLY_BUDGET_MS="$SIMULATE_APPLY_BUDGET_MS"

usage() {
	cat <<EOF
Usage: $0 --stellar-core-image IMAGE [--data-root PATH] [--ingress-external-host HOST]
       [--sac RATE] [--oz RATE] [--soroswap RATE] [--tier1s COUNT]
       [--block-time-ms MS] [--simulate-apply-budget-ms MS] [-- MISSION_ARG...]

Runs one MinBlockTimeMixed mission per selected load flag against IMAGE, with
tx end-to-end latency metrics enabled. Each run always generates
${CLASSIC_TX_RATE} classic payment TPS plus the selected Soroban TPS.
For example, "--sac 50" tests ${CLASSIC_TX_RATE} classic TPS + 50 SAC Soroban
TPS; "--sac 50 --oz 25" runs two separate benchmarks.

Options:
  --stellar-core-image, --image IMAGE   Stellar Core image to evaluate. Required.
  --data-root, --supercluster-root PATH Root containing data/. Defaults to pwd.
  --ingress-external-host HOST          Cluster-external hostname the driver connects to for
                                        the gateway route. Defaults to the route hostname the
                                        mission derives from the internal domain.
  --sac RATE                            Run SAC load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  --oz RATE                             Run OZ load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  --soroswap RATE                       Run Soroswap load with the given Soroban tx rate.
                                        Can be supplied with other load flags to run benchmarks sequentially.
  --tier1s COUNT                        Total number of tier1 orgs to run. Synthetic orgs are
                                        added on top of the ones data/tier1keys.json already
                                        covers, so COUNT may not be below that count. Each added
                                        org runs ${TIER1_ORG_SIZE} validators, which displace ${TIER1_ORG_SIZE} nodes without a
                                        home domain at the fixed network size of ${NETWORK_SIZE_LIMIT}.
                                        Defaults to however many orgs data/tier1keys.json covers.
  --block-time-ms MS                    Ledger target close time to evaluate, in milliseconds.
                                        Also scales the derived apply duration. Defaults to
                                        ${DEFAULT_BLOCK_TIME_MS}, which is also the protocol's maximum.
  --simulate-apply-budget-ms MS         Total synthetic apply sleep to aim for per ledger, in
                                        milliseconds. Divided by the per-ledger operation count
                                        to get the per-operation microseconds the mission takes
                                        (OP_APPLY_SLEEP_TIME_DURATION_FOR_TESTING), so the budget
                                        stays put as the tx rate and close time change.
                                        Defaults to ${DEFAULT_SIMULATE_APPLY_BUDGET_MS}.
  -h, --help                            Show this help.
  -- MISSION_ARG...                     Everything after "--" is appended verbatim to each
                                        mission command line. Use it for one-off flags this
                                        wrapper does not expose.

Benchmark constants:
  Classic TPS:        ${CLASSIC_TX_RATE}
  Network size limit: ${NETWORK_SIZE_LIMIT}
  Data set:           data/${PUBNET_DATA_FILE}
  Loadgen keys:       data/${LOADGEN_KEYS_FILE}

Defaults, overridable by the options above:
  Target close time:  ${DEFAULT_BLOCK_TIME_MS}ms (--block-time-ms), evaluated exactly once because the
                      search band is +/-${BLOCK_TIME_BAND_MS}ms
  Apply budget:       ${DEFAULT_SIMULATE_APPLY_BUDGET_MS}ms per ledger (--simulate-apply-budget-ms), divided by the
                      per-ledger operation count to get microseconds per operation
  Tier1 orgs:         however many data/tier1keys.json covers (--tier1s)

Results:
  PASS: command exits 0 and logs "Minimum sustainable block time: ..." along
        with the e2e latency metrics for each load-generating node.
  FAIL: command exits non-zero, reports no satisfying block time, or reports
        loadgen/sync/consistency failures.
EOF
}

# Parses this wrapper's options and stops at a "--" separator. Sets
# PARSED_ARG_COUNT to the number of arguments consumed (including the
# separator) so the caller can shift them off and keep the mission arguments
# that followed in "$@".
parse_args() {
	total_arg_count="$#"

	while [ "$#" -gt 0 ]; do
		case "$1" in
		--tier1s)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				die "--tier1s requires an org count."
			fi
			TIER1_ORG_COUNT="$2"
			shift 2
			;;
		--tier1s=*)
			TIER1_ORG_COUNT="${1#*=}"
			# An empty value would silently fall back to the data
			# set's own tier1 orgs, so reject it like the two-token
			# form does.
			if [ -z "$TIER1_ORG_COUNT" ]; then
				die "--tier1s requires an org count."
			fi
			shift
			;;
		--block-time-ms)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				die "--block-time-ms requires a close time in milliseconds."
			fi
			BLOCK_TIME_MS="$2"
			shift 2
			;;
		--block-time-ms=*)
			BLOCK_TIME_MS="${1#*=}"
			if [ -z "$BLOCK_TIME_MS" ]; then
				die "--block-time-ms requires a close time in milliseconds."
			fi
			shift
			;;
		--simulate-apply-budget-ms)
			if [ "$#" -lt 2 ] || [ -z "$2" ]; then
				die "--simulate-apply-budget-ms requires a budget in milliseconds."
			fi
			SIMULATE_APPLY_BUDGET_MS="$2"
			shift 2
			;;
		--simulate-apply-budget-ms=*)
			SIMULATE_APPLY_BUDGET_MS="${1#*=}"
			# An empty value would silently fall back to the default
			# budget, so reject it like the two-token form does.
			if [ -z "$SIMULATE_APPLY_BUDGET_MS" ]; then
				die "--simulate-apply-budget-ms requires a budget in milliseconds."
			fi
			shift
			;;
		--)
			shift
			break
			;;
		*)
			base_parse_arg "$@"

			if [ "$BASE_PARSE_SHIFT" -eq 0 ]; then
				die "Unknown argument: $1"
			fi

			shift "$BASE_PARSE_SHIFT"
			;;
		esac
	done

	PARSED_ARG_COUNT=$((total_arg_count - $#))
}

validate_args() {
	if [ -n "$TIER1_ORG_COUNT" ] && ! is_nonnegative_integer "$TIER1_ORG_COUNT"; then
		fail "--tier1s must be a non-negative integer."
	fi
}

# Turns a desired total tier1 org count into the --tier-1-orgs-to-add value the
# mission takes. tier1keys.json is a flat list of public keys carrying no org
# information, so this relies on every tier1 org running exactly
# TIER1_ORG_SIZE validators.
resolve_tier1_orgs_to_add() {
	desired="${1:?usage: resolve_tier1_orgs_to_add COUNT}"

	if [ ! -f "$TIER1_KEYS" ]; then
		printf '%s\n' "--tier1s needs $TIER1_KEYS to count the tier1 orgs already in the network." >&2
		return 1
	fi

	# Splitting on commas puts one JSON entry per line, so this counts keys
	# whether the file is pretty-printed or minified.
	key_count="$(tr ',' '\n' <"$TIER1_KEYS" | grep -c 'publicKey')"

	# grep reports 0 both for a file this does not know how to read and for one
	# that really is empty. Either way there is no org count to infer, and
	# treating it as zero would add the full --tier1s count on top of whatever
	# the file actually holds.
	if [ "$key_count" -eq 0 ]; then
		printf '%s\n' "$TIER1_KEYS holds no publicKey entries; cannot infer an org count for --tier1s." >&2
		return 1
	fi

	if [ "$((key_count % TIER1_ORG_SIZE))" -ne 0 ]; then
		printf '%s\n' "$TIER1_KEYS holds $key_count keys, which is not a multiple of $TIER1_ORG_SIZE; cannot infer an org count for --tier1s." >&2
		return 1
	fi

	existing=$((key_count / TIER1_ORG_SIZE))

	if [ "$desired" -lt "$existing" ]; then
		printf '%s\n' "--tier1s $desired is below the $existing orgs already in $TIER1_KEYS; orgs can only be added." >&2
		return 1
	fi

	printf '%s\n' "$((desired - existing))"
}

parse_args "$@"
shift "$PARSED_ARG_COUNT"
base_validate_args
validate_args
base_resolve_derived

# The mission requires --loadgen-keys for --measure-e2e-latency, and
# --pubnet-data for --loadgen-keys; base.sh always supplies the last of those.
set -- "$@" \
	--loadgen-keys "$DATA_ROOT/data/$LOADGEN_KEYS_FILE" \
	--measure-e2e-latency

if [ -n "$TIER1_ORG_COUNT" ]; then
	tier1_orgs_to_add="$(resolve_tier1_orgs_to_add "$TIER1_ORG_COUNT")" || exit 1
	set -- "$@" --tier-1-orgs-to-add "$tier1_orgs_to_add"
fi

base_run_selected_loads "$@"
