#!/bin/sh

# SLP mixed-load evaluation wrapper.
#
# This script runs the MinBlockTimeMixed mission against a stellar-core image
# using the 2026-06-03 pubnet topology data and the fixed benchmark parameters
# below. It is intended to answer: "does this image sustain the selected mixed
# classic/Soroban load at the normal 5s ledger close target?"
#
# The benchmark setup and how to read the result are documented in
# scripts/base.sh, which holds the shared implementation.

. "$(dirname "$0")/base.sh"

NETWORK_SIZE_LIMIT=277
PUBNET_DATA_FILE="public-network-data-2026-06-03-trimmed-located.json"

usage() {
	cat <<EOF
Usage: $0 --stellar-core-image IMAGE [--data-root PATH] [--ingress-external-host HOST]
       [--sac RATE] [--oz RATE] [--soroswap RATE]

Runs one MinBlockTimeMixed mission per selected load flag against IMAGE.
Each run always generates ${CLASSIC_TX_RATE} classic payment TPS plus the
selected Soroban TPS. For example, "--sac 50" tests ${CLASSIC_TX_RATE} classic
TPS + 50 SAC Soroban TPS; "--sac 50 --oz 25" runs two separate benchmarks.

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
  -h, --help                            Show this help.

Benchmark constants:
  Classic TPS:        ${CLASSIC_TX_RATE}
  Target close time:  ${BLOCK_TIME_MS}ms, evaluated via [$((BLOCK_TIME_MS - BLOCK_TIME_BAND_MS)), $((BLOCK_TIME_MS + BLOCK_TIME_BAND_MS))]
  Network size limit: ${NETWORK_SIZE_LIMIT}
  Data set:           data/${PUBNET_DATA_FILE}

Results:
  PASS: command exits 0 and logs "Minimum sustainable block time: ...".
        With this wrapper, treat that as passing the workload at the normal
        5s target close time.
  FAIL: command exits non-zero, reports no satisfying block time, or reports
        loadgen/sync/consistency failures.
EOF
}

parse_args() {
	while [ "$#" -gt 0 ]; do
		base_parse_arg "$@"

		if [ "$BASE_PARSE_SHIFT" -eq 0 ]; then
			die "Unknown argument: $1"
		fi

		shift "$BASE_PARSE_SHIFT"
	done
}

parse_args "$@"
base_validate_args
base_resolve_derived
base_run_selected_loads
