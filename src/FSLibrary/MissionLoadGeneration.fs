// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionLoadGeneration

open StellarCoreSet
open StellarMissionContext

let loadGeneration (context : MissionContext) =
    let coreSet = MakeCoreSet "core" 3 3 CoreSetOptions.Default
    context.Execute [coreSet] None (fun f ->
        f.WaitUntilSynced [coreSet]
        f.RunLoadgen coreSet context.GenerateAccountCreationLoad
        f.RunLoadgen coreSet context.GeneratePaymentLoad
    )
