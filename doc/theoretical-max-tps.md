# Theoretical Max TPS Test

The theoretical max TPS test is a configuration of the `MaxTPSClassic` mission
that searches for a maximum transaction-per-second rate that stellar-core can
support under ideal circumstances. It uses a network of 7 stellar-core nodes, of
which 3 are validators.  We provide this topology in
[`/topologies/theoretical-max-tps.json`](../topologies/theoretical-max-tps.json).
Each node gets their own EC2 `m5d.4xlarge` instance.

To run the test, first [set up an EKS cluster](eks.md). Accepting the default
settings will produce a topology identical to what we use in our test setup.
Then, run a `MaxTPSClassic` mission with the following template:
```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MaxTPSClassic --image=<core-image> --pubnet-data=<path-to-repo>/topologies/theoretical-max-tps.json --tx-rate=<min-tx-rate> --max-tx-rate=<max-tx-rate> --namespace default --ingress-internal-domain=<domain> --ingress-class=nginx --run-for-max-tps=classic --enable-tcp-tuning
```
For more information about how to set the parameters in the above command, see
[Measuring Transaction Throughput](measuring-transaction-throughput.md).

At the end of the test you should see a line that looks like:
```
Final tx rate: <rate> for image <core-image>
```

Finally, don't forget to shut down your EKS cluster.

## Results

This table contains the theoretical max TPS stellar-core achieved, ordered by
stellar-core release. The columns are as follows:

* **Core Version.** The stellar core release tested.
* **Core Image.** The exact docker image tested, from Docker Hub.
* **Core Compiler Flags.** C/C++ compiler flags used in the stellar-core build.
* **Core Configure Flags.** Arguments passed to stellar-core's `./configure` script.
* **Supercluster Commit.** Supercluster commit hash used to run the test
* **Extra Supercluster Options.** Additional Supercluster commands used beyond the ones specified in the template specified in the introduction of this document.
* **Max TPS.** The result of the test.
* **Notes.** Any additional information on the test result or methodology.

| Core Version | Core Image | Core Compiler Flags | Core Configure Flags | Supercluster Commit | Extra Supercluster Options | Max TPS | Notes |
|--------------|------------|---------------------|----------------------|---------------------|----------------------------|---------|-------|
| 24.1.0 | `stellar/unsafe-stellar-core:24.1.1-2863.5a7035d49.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `ffe34dd872e34152867831b66973910db9a8805d` | `--run-for-max-tps=classic --enable-relaxed-auto-qset-config` | 2902 | |
| 24.0.0 | `stellar/unsafe-stellar-core:24.1.1-2862.0d7b4345d.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `ffe34dd872e34152867831b66973910db9a8805d` | `--run-for-max-tps=classic --enable-relaxed-auto-qset-config` | 2875 | |
| 23.0.1 | `stellar/unsafe-stellar-core:24.0.1-2855.050eacf11.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `ffe34dd872e34152867831b66973910db9a8805d` | `--run-for-max-tps=classic --enable-relaxed-auto-qset-config` | 2742 | Performance improvement due to [tuning TCP settings](https://github.com/stellar/supercluster/pull/311). |
| 23.0.1 | `stellar/unsafe-stellar-core:23.0.2-2688.050eacf11.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `d1c90850c6691b58f4d5a425ee2c57cfb14a790d` | `--run-for-max-tps=classic --enable-relaxed-auto-qset-config` | 2223 | Performance improvement partially due to increased configurability in testing infrastructure. |
| 22.4.1 | `stellar/unsafe-stellar-core:23.0.2-2687.5d4528c33.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `d1c90850c6691b58f4d5a425ee2c57cfb14a790d` | `--run-for-max-tps=classic-prev-version --enable-relaxed-auto-qset-config` | 2108 | |
| 22.3.0 | `stellar/unsafe-stellar-core:23.0.2-2691.e643061a4.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `d1c90850c6691b58f4d5a425ee2c57cfb14a790d` | `--run-for-max-tps=classic-prev-version --enable-relaxed-auto-qset-config` | 2089 | |
| 22.3.0 | `stellar/unsafe-stellar-core:22.3.1-2490.e643061a4.focal-tmtps-perftests` | `-ggdb -O3 -fstack-protector-strong` | | `d9df3be5ec3ea6d7f18262c601b05c793eb2c63b` | `--run-for-max-tps --enable-relaxed-auto-qset-config` | 2032 | Performance improvement due to a combination of new stellar-core performance focused features, and testing methodology changes due to pregenerating transactions for load generation. See [release notes](https://github.com/stellar/stellar-core/releases/tag/v22.3.0) for more info. |
| 22.2.0 | `stellar/unsafe-stellar-core:22.2.0-2361.e6c1f3bfc.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | `b49c0810f159e0305328e127057e1f6fc08a0524` | | 1079 | Performance improvement due to BucketList caching changes |
| 22.1.0rc1 | `stellar/unsafe-stellar-core:22.1.0-2189.rc1.fdd833d57.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 989 | Performance improvement due to [networking changes](https://github.com/stellar/stellar-core/pull/4544) |
| 22.0.0 | `stellar/unsafe-stellar-core:22.0.0-2138.721fd0a65.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 902 | |
| 22.0.0rc2 | `stellar/unsafe-stellar-core:22.0.0-2095.rc2.1bccbc921.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 958 | First version with mandatory BucketListDB backend |
| 21.3.1 | `stellar/unsafe-stellar-core:21.3.1-2007.4ede19620.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 1110 | Used BucketListDB database backend |
| 21.3.1 | `stellar/unsafe-stellar-core:21.3.1-2007.4ede19620.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 1170 | Used SQLite in-memory database backend |
| 21.2.0 | `stellar/unsafe-stellar-core:21.2.0-1953.d78f48eac.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 1059 | Used BucketListDB database backend |
| 21.2.0 | `stellar/unsafe-stellar-core:21.2.0-1953.d78f48eac.focal-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 1053 | Used SQLite in-memory database backend |
| 21.1.0 | `stellar/unsafe-stellar-core:21.0.1-1917.52a449ff3.focal-testing-asan-disabled-perftests` | `-ggdb -O3 -fstack-protector-strong` | `--enable-tracy --enable-tracy-capture --enable-tracy-csvexport` | | | 1137 | Used SQLite in-memory database backend |
