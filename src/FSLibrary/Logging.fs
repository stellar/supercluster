// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module Logging

open Serilog

// These are just F# typesafe-printf wrappers that route to Serilog

let LogDebug fmt = Printf.kprintf (fun s -> Log.Debug(s)) fmt
let LogWarn fmt = Printf.kprintf (fun s -> Log.Warning(s)) fmt
let LogInfo fmt = Printf.kprintf (fun s -> Log.Information(s)) fmt
let LogError fmt = Printf.kprintf (fun s -> Log.Error(s)) fmt
