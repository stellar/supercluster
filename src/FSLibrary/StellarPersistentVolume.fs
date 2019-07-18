// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarPersistentVolume

open Logging
open System.IO

type PersistentVolume(root: string) =
    let root = root
    let mutable volumes = List.empty<string>

    do
        if File.Exists(root)
        then
            IOException (sprintf "%s ia a file. Use different persistent volume root or remove %s" root root) |> raise
        else
            Directory.CreateDirectory(root) |> ignore

    member public self.FullPath volume =
        Path.Combine [|root; volume|]

    member public self.Create volume =
        volumes <- volume :: volumes
        LogInfo "Creating %s" (self.FullPath(volume))
        Directory.CreateDirectory(self.FullPath(volume)) |> ignore

    member public self.Cleanup =
        for volume in volumes do
            try
                Directory.Delete(self.FullPath(volume), true)
            with
            | x -> ()
        volumes <- []
