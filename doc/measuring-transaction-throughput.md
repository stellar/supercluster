# Running maximum transaction per second test

Supercluster supports running missions with artificially generated network topologies. Desired topology must be provided in a particular JSON format:
```
[
  {
    "publicKey": 0,
    "peers": [
      1
    ],
    "sb_homeDomain": "org1"
  },
  {
    "publicKey": 1,
    "peers": [
      0
    ],
    "sb_homeDomain": "org2"
  }
]
```
- “publicKey” is the ID of the node. It can be an actual public key string, or any unique identifier (in the example above numbers are used as IDs)
- “Peers“ is the list of other IDs that this node should connect to
- “sb_homeDomain” is the home domain of the organization this node belongs to
- To mark nodes as “Tier1”, sb_homeDomain must be any of the following: [“sdf”, “pn”, “lo”, “wx”, “cq”, “sp”, “bd”]. Note: right now the Tier1 orgs are hard-coded, since this is the only Tier1 configuration used currently, but in the future this functionality can be improved by allowing arbitrary nodes to specify whether they are Tier1.

To run a mission with an artificial topology, pass `--pubnet-data` option with the generated topology JSON. Your command would roughly look like this:

`dotnet run --project src/App/App.fsproj --configuration Release mission SimulatePubnetTier1Perf --image <stellar-core-docker-image> --pubnet-data generated-overlay-topology.json`

SimulatePubnetTier1Perf runs a binary search trying to find the maximum tx/s rate the network can sustain. It will do so three times and average the results. Look for logs like “Final tx rate averaged to 1000 over 3 runs for image …”.
