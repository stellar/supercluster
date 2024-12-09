# Theoretical Max TPS Test

The theoretical max TPS test is a configuration of the `MaxTPSClassic` mission
that searches for a maximum transaction-per-second rate that stellar-core can
support under ideal circumstances. It uses a network of 7 stellar-core nodes, of
which 3 are validators.  We provide this topology in
[`/topologies/theoretical-max-tps.json`](../topologies/theoretical-max-tps.json).

To run the test, first [set up an EKS cluster](eks.md). Then, run a
`MaxTPSClassic` mission with the following template:
```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MaxTPSClassic --image=<core-image> --netdelay-image=stellar/sdf-netdelay:latest --pubnet-data=<path-to-repo>/topologies/theoretical-max-tps.json --num-runs=<runs> --tx-rate=<min-tx-rate> --max-tx-rate=<max-tx-rate> --namespace default --ingress-internal-domain=<domain> --ingress-class=nginx
```
For more information about how to set the parameters in the above command, see
[Measuring Transaction Throughput](measuring-transaction-throughput.md).

At the end of the test you should see a line that looks like:
```
Final tx rate averaged to <rate> over <runs> runs for image <core-image>
```

Finally, don't forget to shut down your EKS cluster.

## Results

This table contains the theoretical max TPS stellar-core achieved, ordered by
stellar-core release.

| Core Version | Core Image | Database Backend | Topology (total # of stellar-core nodes / # of validators) | EC2 Instance Type | Number of EC2 Instances | Max TPS | Notes |
|--------------|------------|------------------|------------------------------------------------------------|-------------------|-------------------------|---------|-------|
| 22.1.0rc1 | `stellar/unsafe-stellar-core:22.1.0-2189.rc1.fdd833d57.focal-perftests` | BucketListDB | 7 / 3 | m5d.4xlarge | 10 | 989 | Performance improvement due to [networking changes](https://github.com/stellar/stellar-core/pull/4544) |
| 22.0.0 | `stellar/unsafe-stellar-core:22.0.0-2138.721fd0a65.focal-perftests` | BucketListDB | 7 / 3 | m5d.4xlarge | 10 | 902 | |
| 22.0.0rc2 | `stellar/unsafe-stellar-core:22.0.0-2095.rc2.1bccbc921.focal-perftests` | BucketListDB | 7 / 3 | m5d.4xlarge | 10 | 958 | First version with mandatory BucketListDB backend |
| 21.3.1 | `stellar/unsafe-stellar-core:21.3.1-2007.4ede19620.focal-perftests` | BucketListDB | 7 / 3 | m5d.4xlarge | 10 | 1110 | |
| 21.3.1 | `stellar/unsafe-stellar-core:21.3.1-2007.4ede19620.focal-perftests` | SQLite in-memory | 7 / 3 | m5d.4xlarge | 10 | 1170 | |
| 21.2.0 | `stellar/unsafe-stellar-core:21.2.0-1953.d78f48eac.focal-perftests` | BucketListDB | 7 / 3 | m5d.4xlarge | 10 | 1059 | |
| 21.2.0 | `stellar/unsafe-stellar-core:21.2.0-1953.d78f48eac.focal-perftests` | SQLite in-memory | 7 / 3 | m5d.4xlarge | 10 | 1053 | |
| 21.1.0 | `stellar/unsafe-stellar-core:21.0.1-1917.52a449ff3.focal-testing-asan-disabled-perftests` | SQLite in-memory | 7 / 3 | m5d.4xlarge | 10 | 1137 | |
