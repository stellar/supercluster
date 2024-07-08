// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMixedImageNetworkSurvey

// This test runs a survey with 1 old node and 2 new nodes. One of the new nodes
// runs the survey. The test primarily checks that no nodes crash. It also
// checks that HTTP endpoint responses indicate success, but it does not check
// the content of those responses for correctness (for example, it does not
// check the contents of the JSON response to the `getsurveyresult` command).

open Logging
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarCorePeer
open StellarDotnetSdk.Accounts

let mixedImageNetworkSurvey (context: MissionContext) =
    let oldNodeCount = 1
    let newNodeCount = 2

    // Set max survey phase durations to 3 minutes
    let surveyPhaseDurationMinutes = 3

    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage newImage

    let oldName = "core-old"
    let newName = "core-new"

    // keypairs of old and new surveyed nodes
    let oldSurveyedKeys = KeyPair.Random()
    let newSurveyedKeys = KeyPair.Random()

    let oldCoreSet =
        { name = CoreSetName oldName
          keys = [| oldSurveyedKeys |]
          live = true
          options =
              { CoreSetOptions.GetDefault oldImage with
                    nodeCount = oldNodeCount
                    accelerateTime = false
                    surveyPhaseDuration = None } }

    let newCoreSet =
        { name = CoreSetName newName
          keys = [| KeyPair.Random(); newSurveyedKeys |]
          live = true
          options =
              { CoreSetOptions.GetDefault newImage with
                    nodeCount = newNodeCount
                    accelerateTime = false
                    surveyPhaseDuration = Some surveyPhaseDurationMinutes } }

    let coreSets = [ newCoreSet; oldCoreSet ]

    context.Execute
        coreSets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced coreSets

            // Upgrade the network to the old protocol
            let oldPeer = formation.NetworkCfg.GetPeer oldCoreSet 0
            let oldVersion = oldPeer.GetSupportedProtocolVersion()
            formation.UpgradeProtocol coreSets oldVersion

            // Chose a new node to run the survey
            let surveyor = formation.NetworkCfg.GetPeer newCoreSet 0

            // Helper functions to run survey commands from `surveyor`

            // Log a response to a survey command `commandName` and check that
            // it satisfies `predicate`
            let logAndCheckResponse (commandName: string) (response: string) (predicate: string -> bool) =
                LogInfo "%s: %s" commandName response

                if not (predicate response) then
                    failwithf "Survey failed. Unexpected response from '%s': %s" commandName response

            let startSurveyCollecting () =
                let nonce = 42
                let expected = "Requested network to start survey collecting."
                logAndCheckResponse "startSurveyCollecting" (surveyor.StartSurveyCollecting nonce) ((=) expected)

            let stopSurveyCollecting () =
                let expected = "Requested network to stop survey collecting."
                logAndCheckResponse "stopSurveyCollecting" (surveyor.StopSurveyCollecting()) ((=) expected)

            let surveyTopologyTimeSliced (node: KeyPair) =
                // `surveyTopologyTimeSliced` responds differently based on
                // whether this is the first call to it or a subsequent call.
                // However, the response starts with "Adding node." on success.
                let start = "Adding node."

                logAndCheckResponse
                    "surveyTopologyTimeSliced"
                    (surveyor.SurveyTopologyTimeSliced node.AccountId 0 0)
                    (fun s -> s.StartsWith start)

            let getSurveyResult () =
                // This just checks that the survey result starts with `{`,
                // indicating that it returned a JSON object. `getsurveyresult`
                // returns a string (not starting with `{`) on failure.
                logAndCheckResponse "getSurveyResult" (surveyor.GetSurveyResult()) (fun s -> s.StartsWith '{')

            let waitSeconds (seconds: int) =
                LogInfo "Waiting %i seconds" seconds
                System.Threading.Thread.Sleep(seconds * 1000)

            // Start survey collecting
            startSurveyCollecting ()

            // Let survey collect for a minute
            waitSeconds 60

            // Stop survey collecting
            stopSurveyCollecting ()

            // Give message time to propagate
            waitSeconds 30

            // Request results from peers
            surveyTopologyTimeSliced oldSurveyedKeys
            surveyTopologyTimeSliced newSurveyedKeys

            // Give time to propagate and respond
            waitSeconds 60

            // Get results
            getSurveyResult ()

            // Let survey expire. At this point the survey has spent 1.5 minutes
            // in the reporting phase, so knock a minute off of the wait time
            // leaving 30 seconds of buffer to ensure survey is truly expired
            waitSeconds ((surveyPhaseDurationMinutes - 1) * 60)

            // Test collecting phase expiration by starting a new survey and
            // letting it expire.  Add in a buffer to ensure the survey is truly
            // expired
            startSurveyCollecting ()
            waitSeconds (surveyPhaseDurationMinutes * 60 + 30))
