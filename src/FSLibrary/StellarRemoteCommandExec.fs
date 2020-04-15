// Copyright 2020 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarRemoteCommandExec

open Logging
open k8s
open k8s.Models
open StellarCoreSet
open StellarFormation
open StellarShellCmd
open System.Threading
open Microsoft.Rest.Serialization
open CSLibrary

type StellarFormation with

    // Execute a ShCmd on a given peer. This uses the unpleasant but necessary lower-level
    // MuxedStreamNamespacedPodExecAsync function (via a C# helper, for silly async-tasking
    // reasons, F# uses its own thing) and feeds the command string to a remote /bin/sh on
    // stdin, rather than calling the higher-level NamespacedPodExecAsync and passing the
    // command as as a string argument to /bin/sh -c.
    //
    // This is because (astonishingly!) the latter URL-encodes the argv in question, and the
    // URL-encoding of _spaces_ used by the latter (%20-based) is different from the decoding done
    // on k8s side (+-based), so any commands with spaces (eg. all the composite ones) will
    // fail. Stdin (over websockets!)is a little more robust.

    member self.RunRemoteCommand(peer: PodName, cmd: ShCmd) : unit =
        let cmdStr = cmd.ToString()
        let truncated = if cmdStr.Length > 20
                        then cmdStr.Substring(0, 20) + "..."
                        else cmdStr
        LogInfo "Running %d-byte shell command on peer %s: %s" cmdStr.Length peer.StringName truncated

        // We're feeding /bin/sh a command on stdin, which means we also need to run an exit
        // command at the end to ensure it actually terminates instead of sitting there.
        let fullCmdWithExit = ShCmd.ShSeq [|cmd; ShCmd.OfStrs [|"exit"; "0"|] |]

        // Further, we also have to add a trailing "\n" to get it to run at all.
        let fullCmdStr = fullCmdWithExit.ToString() + "\n"

        let res = RemoteCommandRunner.RunRemoteCommand(kube = self.Kube,
                                                       ns = self.NetworkCfg.NamespaceProperty,
                                                       podName = peer.StringName,
                                                       containerName = "stellar-core-run",
                                                       shellCmdStrIncludingNewLine = fullCmdStr)
        if res <> 0
        then
            begin
                LogError "Command failed on peer %s: %s => exited %d " peer.StringName truncated res
                failwith "remote command execution failed"
            end
