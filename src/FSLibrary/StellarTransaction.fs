module StellarTransaction

open stellar_dotnet_sdk

open StellarNetworkCfg
open StellarCoreCfg

type NetworkCfg with

    // The Java and .NET SDKs are missing a method for this, perhaps reasonably.
    member self.RootAccount : Account =
        let networkId = (PrivateNet self.networkNonce).ToString()
        let bytes = System.Text.Encoding.UTF8.GetBytes(networkId)
        let netHash = Util.Hash bytes
        let seq = System.Nullable<int64>(0L)
        new Account(KeyPair.FromSecretSeed(netHash).AccountId, seq)

    member self.TxNewAccount : Transaction =
        let root = self.RootAccount
        let newKey = KeyPair.Random()
        let trb = Transaction.Builder(self.RootAccount)
        trb.AddOperation(CreateAccountOperation(newKey, "10000")).Build()
