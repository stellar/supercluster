// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarDestination

open System.IO

type Destination(path: string) =
    let path = path

    do
        if File.Exists(path)
        then
            IOException (sprintf "%s ia a file. Use different destination or remove %s" path path) |> raise
        else
            Directory.CreateDirectory(path) |> ignore

    member private self.GetExistingNsPath (ns:string) : string =
        let fullPath = Path.Combine [|path; ns|]
        Directory.CreateDirectory(fullPath) |> ignore
        path

    member public self.WriteStream ns name (stream : Stream) =
        let fullPath = Path.Combine [|self.GetExistingNsPath ns; name|]
        let fileStream = File.OpenWrite fullPath
        stream.CopyTo(fileStream)
        fileStream.Close()

    member public self.WriteString (ns:string) (name:string) (content:string) : unit =
        let fullPath = Path.Combine [|self.GetExistingNsPath ns; name|]
        File.WriteAllText(fullPath, content)
