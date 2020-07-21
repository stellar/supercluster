// Copyright 2020 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module ApiRateLimit

open Logging

let mutable apiCallStopwatch = System.Diagnostics.Stopwatch.StartNew()
let mutable lastApiCallTimeInMs : int64 = int64(0)

// We have a given number of API calls per second we can make. We convert
// this to a target number of milliseconds to wait _between_ calls, and
// sleep until that number has passed anytime we are about to make a
// new API call. Naturally this only works if the API call rate it less
// than 1000 req/sec but the default is 30 so this should be fine.
let sleepUntilNextRateLimitedApiCallTime (callsPerSec:int) =
    if callsPerSec > 1000
    then failwith "API rate limit must be <= 1000"
    let msPerCall = int64(1000) / int64(callsPerSec)
    let msSinceLastCall = apiCallStopwatch.ElapsedMilliseconds - lastApiCallTimeInMs
    assert(msSinceLastCall >= int64(0))
    if msSinceLastCall >= msPerCall
    then
       // LogDebug "time since last API call was %d ms, need to wait only %d ms, so not sleeping" msSinceLastCall msPerCall
       ()
    else
        let toSleep = int(msPerCall - msSinceLastCall)
        // LogDebug "sleeping %d ms between API calls" toSleep
        System.Threading.Thread.Sleep(toSleep)
    lastApiCallTimeInMs <- apiCallStopwatch.ElapsedMilliseconds
