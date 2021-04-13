# Notes on F# ("FSharp")

Stellar Supercluster is written primarily in [F#](https://docs.microsoft.com/en-us/dotnet/fsharp/) (with some  [C#](https://docs.microsoft.com/en-us/dotnet/csharp/)) and uses statically typed [.NET bindings to the Kubernetes API](https://github.com/kubernetes-client/csharp), and the statically typed [.NET port of the Stellar SDK](https://github.com/elucidsoft/dotnet-stellar-sdk).

Type-aware language support for F# is available in various .NET IDEs: [VSCode](https://code.visualstudio.com/), [Visual Studio](https://visualstudio.microsoft.com/), [JetBrains Rider](https://code.visualstudio.com/), even [Emacs](https://github.com/fsharp/emacs-fsharp-mode) and [Vim](https://github.com/fsharp/vim-fsharp)).

F# is an ML-family language, similar in many ways to OCaml, but with smooth interoperation with the rest of the .NET ecosystem. If you have not used an ML-family language before, they take a little getting used to, but have many very desirable capabilities. Some initial orientation:

  - If you've written a functional language before (especially one with typed), the cheatsheet (in [HTML](http://dungpa.github.io/fsharp-cheatsheet/) or [PDF](https://github.com/dungpa/fsharp-cheatsheet/raw/gh-pages/fsharp-cheatsheet.pdf)) may be adequate.

  - There's a long list of further [learning resources on fsharp.org](https://fsharp.org/learn.html), including many books and a tutorial site [fsharpforfunandprofit.com](https://fsharpforfunandprofit.com/).

  - A few brief points if you just want to try reading and see how it goes, here:

    - Everything is _immutable by default_. This is not true of the C# objects bound-to inside the .NET ecosystem or the Kubernetes objects they represent, of course; but inside F# you have to ask for mutability in order to get it, otherwise bindings (`let` variables, function arguments, fields in records, etc.) are immutable.

    - Everything has a _static type_, and most are inferred. Everything is typed, but you don't have to write most of the types. You _can_ however write them in many contexts -- by writing `binding:type` -- so put them in places where you want to cross-check a type, or leave one as documentation.

    - Relatively lightweight and fine-grained _"algebraic" type declarations_ are used much more ubiquitously in F# (and all MLs) than in other languages. The two main types are _records_ (declared with `type t = {a:ty; b:ty; ...}`, fields accessable with standard `x.a` dot-syntax) and _discriminated unions_ (declared with `type t = A(ty) | B(ty) | C(ty)`). The latter are really important and different _feeling_ than OO programming: any logic or domain of discourse that has 2 or more cases typically gives rise to a disjoint union to segregate the cases, rather than a class hierarchy; cases are then handled (separately and compiler-checked) with `match` expressions.

    - Everything is an _expression_, including conditionals, loops, declaration blocks. Everything nests and everything evaluates to some value. There's a trivial "unit" value denoted `()` that occurs in contexts that are a bit like `void` or empty argument lists in C, but slightly different. If you see a function taking or being called-with `()`, like `foo()`, the `()` symbol there is an actual value being (logically) passed, it just as a single trivial (0-byte) inhabitant. You can bind that `()` value to a variable, store it in a structure, etc.

    - Function application is by juxtaposition: `foo bar` calls the function `foo` with the argument `bar`.

    - Parentheses usually surround comma-expressions, which build tuples, and many functions take tuples as arguments, so many calls look a bit like parentheses are part of calls, like `foo(bar, baz)`. Especially method calls and calls mapped into F# from other .NET languages like C#. But that is not a mandatory aspect of calling a function: it's calling a function and passing a 2-tuple. It's kinda a silly pun, but it is confusing if you are expecting parentheses to be mandatory on function calls.

    - Functions can be bound, like values, using `let`. A `let` that takes a parameter is a function. For example, `let foo x = x + 1` bound `foo` to the function that adds 1 to its argument.

    - Other functions are attached to types and participate in .NET CLR standard receiver-based method dispatch, supporting self-polymorphism, overriding and inheritence, and so forth. These are called "methods" and are declared with `member` or `override` blocks attached to type declarations.

    - In F# the argument-tuple pun is extended to support _keyword_ arguments, supporting calls like `foo(a=b, c=d)`. These only work on method definitions, not let-bound functions.
