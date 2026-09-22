# Mars Rover Photos

Reads a list of dates from a text file, calls the [NASA Mars Rover Photos API](https://api.nasa.gov)
for each valid one, and downloads the first few photos for a chosen rover to a local folder.

Two ways to run it: a console app (`MarsRoverPhotos.Cli`) and a small JSON API with a one-page UI
(`MarsRoverPhotos.Api`). Both sit on top of the same `MarsRoverPhotos.Core` library, so the parsing,
fetching, storage and error handling logic exists in exactly one place.

## Project structure

```
src/
  MarsRoverPhotos.Core/    date parsing, the NASA client, the file store, the pipeline, DI wiring
  MarsRoverPhotos.Cli/     console host: reads dates.txt, prints a summary table, sets exit code
  MarsRoverPhotos.Api/     minimal API host: GET/POST /api/run, static UI, serves the photos folder
tests/
  MarsRoverPhotos.Tests/   xUnit tests for every Core component, mirroring the folder layout above
dates.txt                  the sample input file from the exercise, at the repo root
Dockerfile, .dockerignore  container build for the API
```

Core has no dependency on either host. Both hosts call a single `AddMarsRoverPhotos(configuration)`
extension method, so the CLI and the API are wired identically and cannot drift apart.

## How to run it

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download).

### Get a NASA API key

`DEMO_KEY` works but is rate limited to roughly 30 requests/hour/IP, shared across everyone
currently using it — in practice it is often already exhausted. Get a free personal key at
<https://api.nasa.gov> (instant, just an email address) and set it as an environment variable.
**The key is never hardcoded anywhere in this repo**; `appsettings.json` only ever ships the
`DEMO_KEY` placeholder.

```
# PowerShell
$env:NASA_API_KEY = "your-real-key"

# bash
export NASA_API_KEY=your-real-key
```

### Console app

```
cd src/MarsRoverPhotos.Cli
dotnet run
```

Reads `dates.txt` from the repository root by default and prints a summary table:

```
Mars rover photo run: curiosity
Started 2026-09-22 11:44:36
Output folder: C:\...\mars-rover-photos\photos

 #  INPUT           PARSED      DOWNLOADED  SKIPPED  FAILED  FOLDER
-------------------------------------------------------------------
 1  02/27/17        2017-02-27           5        0       0  photos\2017-02-27
 2  June 2, 2018    2018-06-02           5        0       0  photos\2018-06-02
 3  Jul-13-2016     2016-07-13           5        0       0  photos\2016-07-13
 4  April 31, 2018  INVALID              0        0       0  -
    ! 'April 31, 2018' looks like a date, but it is not a real one: April 2018 has 30 days, so day 31 does not exist.

Totals: 4 dates, 3 valid, 1 invalid, 15 downloaded, 0 already present, 0 failed
Elapsed: 4.32 s
```

Useful switches:

```
dotnet run -- --dates-file path/to/other-dates.txt   # or -f, or a bare positional path
dotnet run -- --rover opportunity                     # or -r
dotnet run -- --max-photos 3                          # or -n
dotnet run -- --Storage:MaxParallelDownloads=8         # any config key works this way
dotnet run -- --help
```

A relative `--dates-file`/`-f` path (or the bare positional path) is resolved against the directory
you ran the command from, the way any CLI tool would resolve it. The checked-in default
(`dates.txt` with no argument) resolves against the repository root instead, so `dotnet run` finds
it regardless of which `bin/Debug/...` folder it is actually executing from.

Exit codes: `0` every date came back clean, `1` the run finished but at least one date had a
validation or API error, `2` the run could not complete at all (missing file, bad configuration,
Ctrl+C).

### JSON API + minimal UI

```
cd src/MarsRoverPhotos.Api
dotnet run
```

Open the URL it prints (something like `http://localhost:5115`) in a browser for the UI, or call it
directly:

```
GET  /health
GET  /api/run?rover=curiosity&maxPhotos=5&datesFile=dates.txt
POST /api/run   { "dates": ["02/27/17", "June 2, 2018"] }
GET  /photos/2017-02-27/<file>     (read-only static files, once photos exist)
```

### Tests

```
dotnet test
```

63 tests across date parsing, the NASA client, the file store/downloader, and the pipeline.

### Docker

```
docker build -t mars-rover-photos .
docker run --rm -p 8080:8080 -e NASA_API_KEY=your-real-key -v mars-photos:/app/photos mars-rover-photos
```

## Assumptions made

- **Rover and photo count are configurable, not fixed.** The exercise says "use any rover you
  want" and "at least the first 3-5 photos". Default is Curiosity, 5 photos per date, both
  overridable via config or CLI switch.
- **`dates.txt` lives at the repository root**, matching where the exercise says to create it, not
  inside a project folder. Both hosts copy it into their own output directory at build time so
  `dotnet run` works out of the box.
- **A two-digit year always means 20nn**, never 19nn. Safe here because dates before 2004-01-04
  (Spirit's landing, the earliest date any rover photo can carry) are rejected anyway, and dates in
  the future are rejected too, so 19nn could never be a valid answer.
- **Day-before-month numeric dates are not accepted** (`03/04/2016` is read as March 4th, not
  4 March). Accepting both orders would make the same input mean two different photos depending on
  which format matched first, which is worse than rejecting it. Day-first *is* accepted when the
  month is spelled out (`13-Jul-2016`), where the order cannot be misread.
- **"Do not re-download" means skip on disk, not skip via a database.** A photo is considered
  already downloaded if a non-empty file already exists at its expected path. A zero-byte leftover
  from an interrupted run is treated as absent and re-fetched.
- **Dates are processed sequentially**, photos within a date download with bounded concurrency
  (default 4 at once). NASA's real rate limits, especially on `DEMO_KEY`, made concurrent dates a
  bad trade for a take-home exercise.
- **The photos folder is not committed.** It's build output, like `bin/` or `obj/`, and is in
  `.gitignore`.

## A note on the NASA API during development

At the time of writing, `api.nasa.gov/mars-photos/api/v1` briefly returned a `404 No such app`
(its backend is Heroku-hosted and the router returned that for a sleeping/misrouted instance). That
resolved on its own; the endpoint used by this project is the correct, documented one for the
exercise. What is still true is that `DEMO_KEY` is heavily shared and rate limited, so a real key
(free, instant, at <https://api.nasa.gov>) is recommended before the walkthrough. Every layer of
this project (the NASA client, the pipeline, both hosts) is built to treat a `429`, a timeout, or
any other API failure as a per-date error to report, never a crash — that behavior is exercised
directly by `NasaMarsPhotoClientTests` and `PhotoPipelineTests`, and was also verified manually
against the live, rate-limited endpoint during development.

## `AI_NOTES.md`

See [AI_NOTES.md](AI_NOTES.md) for the required write-up of how AI assistance was used to build this.
