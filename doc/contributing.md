# Contributing

Contributions are welcome if they do not interfere with SDF's use of SSC as test
infrastructure.

SSC is primarily written for SDF's own testing needs, however, and generalizing
it beyond that use-case may be out of scope. For any substantial changes, please
file an issue to discuss before getting too far into development.

Please ensure unit tests pass using `dotnet test` and check a few missions using
`dotnet run [...] mission [...]`.

Please also format code before submitting a PR. Code formatting uses
[fantomas](https://github.com/fsprojects/fantomas). You can install it using
`dotnet tool restore` and run it with `dotnet tool run fantomas -r src`

The CI script will check all of these things.
