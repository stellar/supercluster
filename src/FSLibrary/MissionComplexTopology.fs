// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionComplexTopology

open StellarCoreSet
open StellarMissionContext

let complexTopology (context : MissionContext) =
    let coreSet = MakeCoreSet "core" 4 4
                    { CoreSetOptions.Default with
                        quorumSet = Some(["core"])
                        accelerateTime = true }
    let publicSet = MakeCoreSet "public" 2 2
                      { CoreSetOptions.Default with
                          quorumSet = Some(["core"])
                          accelerateTime = true
                          initialization = { CoreSetInitialization.Default with forceScp = false }
                          validate = false }
    let orgSet = MakeCoreSet "org" 1 1
                   { CoreSetOptions.Default with
                       quorumSet = Some(["core"])
                       peers = Some(["public"])
                       accelerateTime = true
                       initialization = { CoreSetInitialization.Default with forceScp = false }
                       validate = false }

    context.Execute [coreSet; publicSet; orgSet] None (fun f ->
        f.WaitUntilSynced [coreSet; publicSet; orgSet]
        f.RunLoadgen coreSet context.GenerateAccountCreationLoad
        f.RunLoadgen coreSet context.GeneratePaymentLoad
    )
