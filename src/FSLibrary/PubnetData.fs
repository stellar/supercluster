// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module PubnetData


// This module provides a set of distributions observed from pubnet. We last
// updated the distributions in October 2024. The apply duration and weight
// distributions are from replaying 5000 recent pubnet ledgers. The other
// distributions contain 3 months of soroban data starting from 6/24/24.
// The soroban distributions re represented as a list of (value, weight) pairs,
// where the weight is in parts-per-thousand.
//
// The intention is to use these distributions to enable testing of core under
// real-world conditions, with a particular focus on overlay performance. Given
// that the overlay is the primary focus, we've trimmed the tails of the
// instructions, read/write bytes, and data entries distributions as these tend
// to overload the relative low CPU counts we're able to allocate in
// supercluster. Instead, we make up for the impact of more cpu-intensive
// transactions by modeling apply-time using the apply duration and weight
// distributions. We do not trim the transaction size or wasm size distributions
// so that the overlay remains realistically stressed.

// Use the `histogram-generator.py` script in the `scripts` directory to
// generate histograms from Hubble data.

let pubnetApplyDuration =
    seq {
        20
        38
        97
        425
        990
    }


let pubnetApplyWeight =
    seq {
        25
        50
        20
        4
        1
    }

// NOTE: This distribution has been trimmed. See module-level comment for more
// info.
let pubnetInstructions = [ (2000000, 1000) ]

// NOTE: This distribution has been trimmed. See module-level comment for more
// info.
let pubnetTotalKiloBytes = [ (2, 1000) ]

let pubnetTxSizeBytes =
    [ (484, 105)
      (876, 43)
      (1268, 76)
      (1660, 261)
      (2052, 255)
      (2444, 204)
      (2836, 33)
      (3228, 18)
      (3620, 2)
      (4012, 3) ]

let pubnetWasmBytes =
    [ (3333, 296)
      (9512, 202)
      (15691, 116)
      (21870, 163)
      (28049, 77)
      (34228, 94)
      (40407, 26)
      (46586, 13)
      (52765, 9)
      (58944, 4) ]

// NOTE: This distribution has been trimmed. See module-level comment for more
// info.
// Also note that the data entries distribution is skewed so that in most cases
// cases there are more kilobytes of I/O than data entries. This is to avoid a
// problem in which loadgen rounds the size of each data entry up (from 0kb to
// 1kb) and underestimates the resources required for the write. This will be
// fixed as part of stellar-core issue #4231.
let pubnetDataEntries = [ (1, 667); (2, 333) ]

let pubnetPayWeight = 99
let pubnetInvokeWeight = 1
let pubnetUploadWeight = 0
