// Copyright 2025 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module ScriptUtils

open System
open System.Diagnostics
open System.IO
open Logging

// Run an external command (e.g. helm) and return its stdout on success, None on failure.
// Failures are logged but do not throw.
let RunShellCommand (command: string []) : string option =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- command.[0]
        psi.Arguments <- String.Join(" ", command.[1..])
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use ps = Process.Start(psi)
        ps.WaitForExit()

        let output = ps.StandardOutput.ReadToEnd()
        let error = ps.StandardError.ReadToEnd()

        if ps.ExitCode <> 0 then
            LogError "Command '%s' failed with error: %s" (String.Join(" ", command)) error
            None
        else
            Some(output)
    with ex ->
        LogError "Command execution failed: %s" ex.Message
        None

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
