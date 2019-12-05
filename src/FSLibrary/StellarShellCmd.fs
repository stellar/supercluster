// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarShellCmd
open System.Text.RegularExpressions

// This is a small set of helper types for composing (a subset of) Bourne shell
// (/bin/sh) commands while preserving a relatively high likelihood that the
// composition will parse correctly in sh and do what you meant. This means it
// avoids sh language constructs that have complicated composition rules or
// require escaping to preserve composition (eg. most of sh's expansion phases).
//
// Specifically:
//
//    - It avoids nesting any _composite_ forms inside quotes, including
//      anything that could produce quotes within quotes, avoiding the need
//      to track quoting levels or perform escaping.
//
//    - It requires that words remain single words, avoiding field splitting
//      or path expansion.
//
// If you have a more complex task, before expanding this grammar to make it
// more expressive (and error-prone), try evaluating your task up here in F# and
// embedding the literal value in ShQuot here. It's the best choice 90% of the
// time. Shell quoting and expansion is kinda a disaster.
//
// NB: this is not a security device. Do not feed it untrusted user input.
// It's a "try not to shoot yourself in the foot while writing sh" device.
// There are an uncountably infinite number of ways of breaking out of sh,
// never feed untrusted user input to sh, at all.

type ShName =
    | ShName of string     // Unquoted [a-zA-Z_][a-zA-Z0-9_]*, usable as a variable
                           // name, allowed to be used as an unquoted word as well.

    static member NameRx = Regex("^[a-zA-Z_][a-zA-Z0-9_]*$");
    static member IsName s = ShName.NameRx.Match(s).Success

    override self.ToString() =
        match self with
            | ShName s ->
                if ShName.IsName s
                then s
                else failwith ("ShName used with non-Name '" + s + "'")

type ShPiece =
    | ShBare of string    // Bare word without quotes, limited values allowed.
    | ShVar of ShName     // Name used as parameter-expansion, prints as ${VAR}.
    | ShSpecialLastExit   // The special $? parameter -- last exit status.

    static member BareRx = Regex("^[a-zA-Z0-9_./-]*$");
    static member IsBare s = ShPiece.BareRx.Match(s).Success

    override self.ToString() =
        match self with
            | ShBare s -> s
            | ShVar s -> "${" + s.ToString() + "}"
            | ShSpecialLastExit -> "${?}"


type ShWord =
    | ShPieces of ShPiece array // Concatenation of pieces, possibly in double-quotes.
    | ShQuoted of string       // Single-quoted string, must have no single-quote in it.

    static member OfStr (s:string) : ShWord =
        if ShPiece.IsBare s
        then ShPieces [| ShBare s |]
        elif s.StartsWith "${" && s.EndsWith "}"
        then ShPieces [| ShVar(ShName(s.Substring(2, (s.Length - 3)))) |]
        else ShQuoted s

    static member Var(s:string) : ShWord =
        assert ShName.IsName s
        ShPieces [| ShVar(ShName s) |]

    override self.ToString() =
        match self with
            | ShPieces [| ShBare n |] -> n.ToString()
            | ShPieces ps ->
                "\"" + String.concat "" (Array.map (sprintf "%O") ps) + "\""
            | ShQuoted s ->
                if s.Contains("'")
                then failwith ("ShQuot used with inner quote '" + s + "'")
                else "'" + s + "'"


type ShRedir =
    | ShRedirFdFrom of (int * ShWord)    // fd<word
    | ShRedirFdTo of (int * ShWord)      // fd>word
    | ShRedirFdDupFrom of (int * int)    // fd>&other
    | ShRedirFdDupTo of (int * int)      // fd<&other

    override self.ToString() =
        match self with
            | ShRedirFdFrom (i, w) -> i.ToString() + "<" + w.ToString()
            | ShRedirFdTo (i, w) -> i.ToString() + ">" + w.ToString()
            | ShRedirFdDupFrom (i, j) -> i.ToString() + "<&" + j.ToString()
            | ShRedirFdDupTo (i, j) -> i.ToString() + ">&" + j.ToString()


// NB: The ShDefSub form below only does substitution on simple "word-list"
// commands, and can therefore not contain itself. Don't change this.
type ShCmd =
    | ShCmd of ShWord array                      // word word word ... (as a command)
    | ShDef of (ShName * ShWord)                 // name=word
    | ShDefSub of (ShName * ShWord array)        // name=`word word word ...`
    | ShRedir of (ShCmd * ShRedir)               // cmd >foo (see ShRedir)
    | ShPipe of ShCmd array                      // { cmd | cmd | ...; }
    | ShAnd of ShCmd array                       // { cmd && cmd && ...; }
    | ShOr of ShCmd array                        // { cmd || cmd || ...; }
    | ShSeq of ShCmd array                       // { cmd; cmd; ...; }
    | ShWhile of (ShCmd * ShCmd)                 // while cmd; do cmd; done
    | ShUntil of (ShCmd * ShCmd)                 // until cmd; do cmd; done
    | ShFor of (ShName * (ShWord array) * ShCmd) // for name in words; do cmd; done
    | ShIf of (ShCmd * ShCmd *                   // if cmd; then cmd;
               (ShCmd * ShCmd) array *           // elif cmd; then cmd;
               ShCmd option)                     // else cmd;
                                                 // fi

    static member True () : ShCmd =
        ShCmd [| ShWord.OfStr "true" |]

    static member OfStr (s:string) : ShCmd =
        ShCmd [| ShWord.OfStr s |]

    static member OfStrs (ss:string array) : ShCmd =
        ShCmd (Array.map ShWord.OfStr ss)

    static member DefVarSub (s:string) (ss:string array) : ShCmd =
        ShDefSub (ShName s, Array.map ShWord.OfStr ss)

    static member IfThen (i:string array) (t:string array) : ShCmd =
        ShIf ((ShCmd.OfStrs i), (ShCmd.OfStrs t), [| |], None)

    static member While (w:string array) (d:string array) : ShCmd =
        ShWhile ((ShCmd.OfStrs w), (ShCmd.OfStrs d))

    static member Until (w:string array) (d:string array) : ShCmd =
        ShWhile ((ShCmd.OfStrs w), (ShCmd.OfStrs d))

    member self.PipeTo (ss:string array) : ShCmd =
        let next = ShCmd.OfStrs ss
        match self with
            | ShPipe cmds -> ShPipe (Array.append cmds [| next |])
            | _ -> ShPipe [| self; next |]

    member self.AndAlso (ss:string array) : ShCmd =
        let next = ShCmd.OfStrs ss
        match self with
            | ShAnd cmds -> ShAnd (Array.append cmds [| next |])
            | _ -> ShAnd [| self; next |]

    member self.OrElse (ss:string array) : ShCmd =
        let next = ShCmd.OfStrs ss
        match self with
            | ShOr cmds -> ShOr (Array.append cmds [| next |])
            | _ -> ShOr [| self; next |]

    member self.Then (ss:string array) : ShCmd =
        let next = ShCmd.OfStrs ss
        match self with
            | ShSeq cmds -> ShSeq (Array.append cmds [| next |])
            | _ -> ShSeq [| self; next |]

    member self.StdOutTo (f:string) : ShCmd =
        ShRedir (self, (ShRedirFdTo (1, ShWord.OfStr f)))

    member self.StdErrTo (f:string) : ShCmd =
        ShRedir (self, (ShRedirFdTo (2, ShWord.OfStr f)))

    member self.StdErrToOut (f:string) : ShCmd =
        ShRedir (self, (ShRedirFdDupTo (2, 1)))

    member self.StdInFrom (f:string) : ShCmd =
        ShRedir (self, (ShRedirFdFrom (0, ShWord.OfStr f)))

    override self.ToString() =
       let objs os = (Array.map (sprintf "%O") os)
       let words ws = String.concat " " (objs ws)
       let seq cs sep =
           "{ " + (String.concat sep (objs cs)) + "; }"
       match self with
           | ShCmd ws -> words ws
           | ShDef (n, v) -> sprintf "%O=%O" n v
           | ShDefSub (n, ws) -> sprintf "%O=`%s`" n (words ws)
           | ShRedir (w, r) -> sprintf "%O %O" w r
           | ShPipe cs -> seq cs " | "
           | ShSeq cs -> seq cs "; "
           | ShAnd cs -> seq cs " && "
           | ShOr cs -> seq cs " || "
           | ShWhile (c, d) ->
               sprintf "while %O; do %O; done" c d
           | ShUntil (u, d) ->
               sprintf "until %O; do %O; done" u d
           | ShFor (n, ws, d) ->
               sprintf "for %O in %s; do %O; done" n (words ws) d
           | ShIf (i, t, eis, eo) ->
               let ei (i, t) = sprintf " elif %O; then %O;" i t
               let eiss = String.concat "" (Array.map ei eis)
               let es = match eo with
                            | Some e -> sprintf " else %O;" e
                            | None -> ""
               sprintf "if %O; then %O;%s%s fi" i t eiss es
