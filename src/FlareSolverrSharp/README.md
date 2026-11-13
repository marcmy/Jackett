# Jackett's FlareSolverrSharp integration

Vendored from FlareSolverr/FlareSolverrSharp 4.0.0, commit
`86027526c27cc32ff4f035535e16dd7c85763dea`. The upstream MIT license is
preserved in `LICENSE`.

Jackett uses this project instead of the NuGet package to fix its global solve
queue and cancellation handling. Each solver endpoint permits two concurrent
requests. Queue waiting is cancellable and limited to five minutes; active
solver requests have the configured challenge budget plus 30 seconds for browser
cleanup. Caller cancellation flows through both phases.

Jackett disables the outer HttpClient timeout. ClearanceHandler applies the
configured tracker timeout independently to the initial request and the retry,
including their response bodies. Queue waiting cannot expire the tracker retry.
Other consumers must also disable the outer HttpClient timeout to use this
separate queue budget.

Regression tests live in Jackett.Test/Common/Utils/FlareSolverrIntegrationTests.cs.
