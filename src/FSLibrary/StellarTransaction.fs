module StellarTransaction

open stellar_dotnet_sdk

open StellarNetworkCfg
open StellarCorePeer
open StellarCoreHTTP

type Username =

    // Good actors
    | UserAlice
    | UserBob
    | UserCarol
    | UserDan

    // Bad actors
    | UserEve
    | UserMallory
    | UserSybil of int
    | UserTrudy

    override self.ToString() =
        match self with
        | UserAlice -> "alice"
        | UserBob -> "bob"
        | UserCarol -> "carol"
        | UserDan -> "dan"
        | UserEve -> "eve"
        | UserMallory -> "mallory"
        | UserSybil n -> sprintf "sybil-%d" n
        | UserTrudy -> "trudy"


let rec Exactly32BytesStartingFrom (s: string) : byte array =
    assert (System.Text.Encoding.ASCII.GetByteCount(s) = s.Length)

    if s.Length > 32 then
        Exactly32BytesStartingFrom(s.Remove 32)
    else if s.Length < 32 then
        Exactly32BytesStartingFrom(s + (String.replicate (32 - s.Length) "."))
    else
        System.Text.Encoding.ASCII.GetBytes(s)

type Peer with

    // The Java and .NET SDKs are missing a method for this, perhaps reasonably.
    member self.RootKeypair : KeyPair =
        let networkId = (PrivateNet self.networkCfg.networkNonce).ToString()
        let bytes = System.Text.Encoding.UTF8.GetBytes(networkId)
        let netHash = Util.Hash bytes
        KeyPair.FromSecretSeed(netHash)

    member self.RootAccount : Account =
        let seq = self.GetTestAccSeq("root")
        Account(self.RootKeypair.AccountId, System.Nullable<int64>(seq))

    member self.GetAccount(u: Username) : Account =
        let key = self.GetKeypair(u)
        let seq = self.GetTestAccSeq(u.ToString())
        Account(key.AccountId, System.Nullable<int64>(seq))

    // Deterministically turn a string key-name into an account, using
    // the same "pad with periods" mechanism used on the testacc HTTP
    // endpoint of stellar-core. NB: this is not a real KDF.
    member self.GetKeypairByName(name: string) : KeyPair =
        let padded = Exactly32BytesStartingFrom name
        assert (padded.Length = 32)
        KeyPair.FromSecretSeed(padded)

    member self.GetKeypair(u: Username) : KeyPair = self.GetKeypairByName(u.ToString())

    member self.GetSeqAndBalance(u: Username) : (int64 * int64) =
        let seq = self.GetTestAccSeq(u.ToString())
        let balance = self.GetTestAccBalance(u.ToString())
        (seq, balance)

    member self.GetNetwork() : Network =
        let phrase = PrivateNet self.networkCfg.networkNonce
        Network(phrase.ToString())

    member self.TxCreateAccount(u: Username) : Transaction =
        let root = self.RootAccount
        let newKey = self.GetKeypair u
        let trb = Transaction.Builder(root)
        let tx = trb.AddOperation(CreateAccountOperation(newKey, "10000")).Build()
        tx.Sign(signer = self.RootKeypair, network = self.GetNetwork())
        tx

    member self.TxPayment (sender: Username) (recipient: Username) : Transaction =
        let send = self.GetAccount sender
        let recv = self.GetKeypair recipient
        let asset = AssetTypeNative()
        let amount = "100"
        let trb = Transaction.Builder(send)

        let tx =
            trb
                .AddOperation(PaymentOperation
                    .Builder(recv, asset, amount)
                    .SetSourceAccount(send.KeyPair)
                    .Build())
                .Build()

        tx.Sign(signer = (self.GetKeypair sender), network = self.GetNetwork())
        tx
