// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarDestination

open System.IO
open Logging

type Destination(path: string) =
    let path = path

    do
        if File.Exists(path) then
            IOException(sprintf "%s ia a file. Use different destination or remove %s" path path)
            |> raise
        else
            Directory.CreateDirectory(path) |> ignore

    member public self.RemoveIfExists name =
        let fullPath = Path.Combine [| path; name |]

        if File.Exists fullPath then
            LogInfo "Deleting %s" fullPath
            File.Delete fullPath

    member public self.WriteStream name (stream: Stream) =
        let fullPath = Path.Combine [| path; name |]
        LogInfo "Copying data stream to %s" fullPath
        let fileStream = File.OpenWrite fullPath
        stream.CopyTo(fileStream)
        fileStream.Close()

    member public self.WriteStreamAsync name (stream: Stream) =
        let fullPath = Path.Combine [| path; name |]
        LogInfo "Asynchronously copying data stream to %s" fullPath
        let fileStream = File.OpenWrite fullPath

        async {
            do! stream.CopyToAsync(fileStream) |> Async.AwaitTask
            fileStream.Close()
        }

    member public self.WriteString (name: string) (content: string) : unit =
        let fullPath = Path.Combine [| path; name |]
        LogInfo "Writing data to %s" fullPath
        File.WriteAllText(fullPath, content)
