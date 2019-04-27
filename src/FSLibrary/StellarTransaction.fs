module StellarTransaction

open stellar_dotnet_sdk

open StellarNetworkCfg
open StellarCoreCfg

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


let rec Exactly32BytesStartingFrom (s:string) : byte array =
    assert(System.Text.Encoding.ASCII.GetByteCount(s) = s.Length)
    if s.Length > 32
    then Exactly32BytesStartingFrom (s.Remove 32)
    else if s.Length < 32
    then Exactly32BytesStartingFrom (s + (String.replicate (32 - s.Length) "."))
    else System.Text.Encoding.ASCII.GetBytes(s)


type NetworkCfg with

    // The Java and .NET SDKs are missing a method for this, perhaps reasonably.
    member self.RootAccount : Account =
        let networkId = (PrivateNet self.networkNonce).ToString()
        let bytes = System.Text.Encoding.UTF8.GetBytes(networkId)
        let netHash = Util.Hash bytes
        let seq = System.Nullable<int64>(0L)
        new Account(KeyPair.FromSecretSeed(netHash).AccountId, seq)

    // Deterministically turn a string key-name into an account, using
    // the same "pad with periods" mechanism used on the testacc HTTP
    // endpoint of stellar-core. NB: this is not a real KDF.
    member self.GetKeypairByName (name:string) : KeyPair =
        let padded = Exactly32BytesStartingFrom name
        assert(padded.Length = 32)
        KeyPair.FromSecretSeed(padded)

    member self.GetKeypair (u:Username) : KeyPair =
        self.GetKeypairByName (u.ToString())

    member self.TxCreateAccount (u:Username) : Transaction =
        let root = self.RootAccount
        let newKey = self.GetKeypair u
        let trb = Transaction.Builder(self.RootAccount)
        trb.AddOperation(CreateAccountOperation(newKey, "10000")).Build()
