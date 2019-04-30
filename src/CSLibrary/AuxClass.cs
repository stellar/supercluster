// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

using Serilog;

// This file just exists to confirm that a mixed-language project works
// i.e. we're not going to have to write strict in F# if we don't want
// to. At the moment I'm happy with F#.

namespace CSLibrary
{
    public class AuxClass
    {
        // Add any code you prefer to write in C# rather than F# here.
        public static void CheckCSharpWorksToo()
        {
            Log.Debug("C# library components available");
        }
    }
}
