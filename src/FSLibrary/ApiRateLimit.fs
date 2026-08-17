// Copyright 2020 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module ApiRateLimit

open Logging
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

let mutable apiCallStopwatch = System.Diagnostics.Stopwatch.StartNew()
let mutable lastApiCallTimeInMs : int64 = int64 (0)

// We have a given number of API calls per second we can make. We convert
// this to a target number of milliseconds to wait _between_ calls, and
// sleep until that number has passed anytime we are about to make a
// new API call. Naturally this only works if the API call rate it less
// than 1000 req/sec but the default is 30 so this should be fine.
let sleepUntilNextRateLimitedApiCallTime (callsPerSec: int) =
    if callsPerSec > 1000 then failwith "API rate limit must be <= 1000"
    let msPerCall = int64 (1000) / int64 (callsPerSec)
    let msSinceLastCall = apiCallStopwatch.ElapsedMilliseconds - lastApiCallTimeInMs
    assert (msSinceLastCall >= int64 (0))

    if msSinceLastCall >= msPerCall then
        // LogDebug "time since last API call was %d ms, need to wait only %d ms, so not sleeping" msSinceLastCall msPerCall
        ()
    else
        let toSleep = int (msPerCall - msSinceLastCall)
        // LogDebug "sleeping %d ms between API calls" toSleep
        System.Threading.Thread.Sleep(toSleep)

    lastApiCallTimeInMs <- apiCallStopwatch.ElapsedMilliseconds

// Retries apiserver 429s so a single rejection cannot end a mission.
type ThrottleRetryHandler(deadline: System.TimeSpan) =
    inherit DelegatingHandler()

    // F# cannot call `base` from inside a task expression, so the base send needs its own member.
    member private this.Send(req: HttpRequestMessage, ct: CancellationToken) = base.SendAsync(req, ct)

    override this.SendAsync(req: HttpRequestMessage, ct: CancellationToken) : Task<HttpResponseMessage> =
        // Deletes are never retried, because every delete site in this library already
        // swallows failure, so retrying buys fewer orphans at the price of multiplying a
        // teardown that removes hundreds of objects in sequence.
        if req.Method = HttpMethod.Delete then
            this.Send(req, ct)
        else
            let sw = System.Diagnostics.Stopwatch.StartNew()

            // Only 429 is retried, because it alone proves the request was rejected unapplied and is safe to re-send.
            let rec attempt backoffMs =
                task {
                    let! r = this.Send(req, ct)
                    let remaining = deadline - sw.Elapsed

                    if r.StatusCode <> HttpStatusCode.TooManyRequests
                       || remaining <= System.TimeSpan.Zero then
                        return r
                    else
                        // Retry-After is a floor, not a replacement, or a server repeating `Retry-After: 1` pins us at one attempt per second.
                        let hint =
                            match r.Headers.RetryAfter with
                            | ra when not (isNull ra) && ra.Delta.HasValue -> int ra.Delta.Value.TotalMilliseconds
                            | _ -> 0

                        // Clamped to what is left, because the wait is otherwise unbudgeted and a long Retry-After would start an attempt past the deadline and past HttpClientTimeout, replacing the 429 with a TaskCanceledException.
                        let waitMs = min (max backoffMs hint) (int remaining.TotalMilliseconds)

                        LogWarn
                            "apiserver throttled %s %s (%O elapsed); retrying in %d ms"
                            req.Method.Method
                            req.RequestUri.PathAndQuery
                            sw.Elapsed
                            waitMs

                        r.Dispose()
                        do! Task.Delay(waitMs, ct)
                        return! attempt (min (backoffMs * 2) 15000)
                }

            task {
                // A request can only be sent once unless its body is buffered first.
                if not (isNull req.Content) then do! req.Content.LoadIntoBufferAsync()
                return! attempt 500
            }
