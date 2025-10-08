// Copyright 2025 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module ScriptUtils

open System
open System.IO

// Helper to resolve script paths that works both in development and deployment
let GetScriptPath (scriptName: string) : string =
    // First, try the deployment location (scripts copied to output directory)
    let deploymentPath = Path.Combine(AppContext.BaseDirectory, "scripts", scriptName)

    if File.Exists(deploymentPath) then
        deploymentPath
    else
        // Fallback to relative to source directory
        let devPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "scripts", scriptName)

        if File.Exists(devPath) then
            devPath
        else
            failwithf "Script not found: %s (looked in %s and %s)" scriptName deploymentPath devPath
