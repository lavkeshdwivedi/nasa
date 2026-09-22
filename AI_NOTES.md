# AI Notes

## Tool used

Claude Code (Anthropic), Opus model. The solution was built by first writing the shared contracts
(the `Abstractions`, `Models` and `Options` types in `MarsRoverPhotos.Core`) by hand, then fanning
those contracts out to six parallel Claude Code sub-agents, each one implementing a single slice
against the interfaces only: date parsing, the NASA client, file storage/download, the pipeline +
DI composition root, the CLI host, and the API host + UI + Dockerfile. A top-level session then
integrated the six slices, ran the full build and test suite, smoke-tested the CLI and API against
the real (rate-limited) NASA endpoint, and fixed the issues that surfaced from that.

## Prompts that worked best

1. **Freezing the interfaces before parallelising.** Every sub-agent prompt included the exact,
   already-written contents of `IDateParser`, `IMarsPhotoClient`, `IPhotoStore` etc. and an explicit
   instruction: *"work only in these files, do not edit anything outside this list, code against the
   interfaces, do not create a stub of another agent's class."* This is the single prompt pattern
   that made six agents writing code at the same time actually merge cleanly, with zero interface
   drift and only one real integration bug (below) once everything came together.

2. **Asking for the *why* behind an ambiguous design choice, not just the code.** For the date
   parser: *"Distinguish these two cases: if the value matches a known format shape but the
   day/month combination does not exist (April 31), say so explicitly, versus a value that is not
   in any recognised format at all."* This produced a parser that gives a genuinely different error
   message for `April 31, 2018` ("looks like a date, but it is not a real one") than for pure
   garbage input ("not in a recognised date format"), which is exactly what the exercise's "Handle
   invalid dates without crashing" and "Log/return errors clearly" requirements are actually asking
   for, rather than a generic try/catch that treats both the same way.

3. **Requiring empirical verification, not just plausible-looking code.** Every sub-agent prompt
   ended with an instruction to actually run `dotnet build`/`dotnet test` on its own slice and report
   which errors were real versus "the other agent hasn't landed yet". Several agents went further
   unprompted and built a throwaway console host to exercise the DI graph end to end before
   reporting done, which is what caught things like `AddStandardResilienceHandler` silently
   overwriting `HttpClient.Timeout` with `Timeout.InfiniteTimeSpan` after the registration callback
   runs, before it became a runtime surprise.

## Where AI generated incorrect/incomplete code, and the fix

The NASA client and the photo downloader were both given `HttpClient`s wired through
`Microsoft.Extensions.Http.Resilience`'s `AddStandardResilienceHandler` (retry + timeout + circuit
breaker). Both were written to catch `HttpRequestException` and `TaskCanceledException` for network
failures, since that is what a plain `HttpClient` throws, but the resilience handler's own timeout
and circuit-breaker strategies throw **`Polly.Timeout.TimeoutRejectedException`** and
**`Polly.CircuitBreaker.BrokenCircuitException`** instead, which are neither of those types. Neither
client caught them.

This wasn't theoretical: it showed up during a real smoke test. With `DEMO_KEY` rate-limited, a live
CLI run against `api.nasa.gov` produced:

```
Fatal: TimeoutRejectedException: The operation didn't complete within the allowed timeout of '00:02:30'.
```

instead of the intended per-date error and a continued run, exactly the "handle network failures
gracefully" requirement, failing. The fix was to add explicit `catch (TimeoutRejectedException)` and
`catch (BrokenCircuitException)` blocks in both `NasaMarsPhotoClient.GetPhotosAsync` and
`HttpPhotoDownloader.DownloadAsync`, wrapping them the same way as the existing network-failure
cases. Re-running the same live scenario afterwards produced the correct behaviour:

```
fail: MarsRoverPhotos.Core.Pipeline.PhotoPipeline[0] Line 1 2017-02-27: NASA request failed, continuing with the remaining dates
```

The pipeline logged the failure against that one date and moved on to the next, and the console
report rendered it as a per-date error rather than aborting the run. This is covered going forward
by the existing failure-handling tests in `NasaMarsPhotoClientTests` and `PhotoPipelineTests`, which
already assert that a client failure produces a per-date error rather than an unhandled exception.

## Other significant changes made after AI generated the code

- **CLI relative-path resolution.** The generated `Program.cs` anchored *every* configured path
  (both the `appsettings.json` default and an explicit `--dates-file`/`-f` override) against the
  repository root, so that `dotnet run` would find the checked-in `dates.txt` regardless of which
  `bin/Debug/net9.0` folder it executed from. That's the right behaviour for the *default*, but it
  meant a path a user typed themselves, e.g. `--dates-file ../../dates.txt` from inside
  `src/MarsRoverPhotos.Cli`, was silently resolved against the wrong base and pointed one directory
  above the actual repository. Fixed by anchoring an explicitly-supplied dates-file path (switch or
  bare positional argument) against the shell's own current directory instead, which is what every
  other CLI tool does with a relative path, while leaving the appsettings.json default anchored to
  the repository root.
- **Process/file-lock cleanup during integration.** A couple of agents left a `dotnet run` process
  alive from their own manual verification, which then held a file lock on `MarsRoverPhotos.Core.dll`
  and made the top-level `dotnet build` fail with `MSB3027`. This is an artifact of parallel
  development, not application code, but it's worth recording as the reason for a couple of stalled
  build attempts during integration.
- **Independent verification of the NASA API itself.** Rather than trusting the exercise's written
  description of the endpoint, the live endpoint was probed directly with `curl`/`Invoke-WebRequest`
  before and during development, using both `DEMO_KEY` and a real personal key. This found that
  `api.nasa.gov/mars-photos/api/v1`'s Heroku-hosted backend is intermittently returning `404 No such
  app` from its router instead of a real response, and, surprisingly, that this varies by key: the
  same endpoint gave `DEMO_KEY` a normal `429` (a healthy backend, just rate limited) while a valid
  personal key consistently hit `404` on the identical URL, for both `earth_date` and `sol` queries.
  The personal key itself was confirmed valid by calling `GET /planetary/apod` with it successfully,
  which narrowed the problem to NASA's own request routing for the mars-photos service rather than
  the key, the request shape, or anything in this codebase. This was also the origin of the CLI/API
  smoke-testing that surfaced the `TimeoutRejectedException` bug above; without probing the real,
  imperfect endpoint directly, that gap would only have shown up once a personal key hit it, which
  in this case turned out to still fail, just with a different, equally ungraceful, exception type.
