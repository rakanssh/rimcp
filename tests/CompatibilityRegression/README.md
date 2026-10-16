# Compatibility regression checks

This Windows .NET Framework console harness loads the built `RiMCP.dll` and real
RimWorld 1.6 managed assemblies. It checks the shared 1.6/1.7 mod without starting
RimWorld, opening a bridge connection, or accessing player configuration or saves.
No game API stubs are used. Definitions and bill objects are created only inside
the test process.

Build the mod first using the repository README. Then, from the repository root:

```powershell
$managed = 'C:\path\to\RimWorld-1.6\Data\Managed'
dotnet build tests/CompatibilityRegression/CompatibilityRegression.csproj -c Release "-p:RimWorldManagedPath=$managed"
& tests/CompatibilityRegression/bin/Release/net472/CompatibilityRegression.exe $managed (Resolve-Path RiMCP/Assemblies/RiMCP.dll).Path
```

Use the same retained 1.6 reference directory used to build the mod. It must
include `Assembly-CSharp.dll` and the corresponding Unity managed dependencies.
The harness intentionally does not run against 1.7's .NET 10 engine assemblies;
live 1.7 smoke tests cover that runtime.

When using the repository-local dependency cache:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
dotnet restore tests/CompatibilityRegression/CompatibilityRegression.csproj --source $env:NUGET_PACKAGES "-p:RimWorldManagedPath=$managed"
```

The forty-six checks cover:

- Main-menu research listing, definition lookup, and definition search, including
  absence of invented `finished`/`progress` values and blockers, and `availableOnly`
  refused without a colony.
- Empty quests, ideology enumeration, and world data without a loaded game.
- A controlled 409 research-command rejection without a game.
- A numeric cursor roundtrip through actual endpoint JSON, including distinct
  subsequent pages and termination on the last page.
- Def search by a def subclass kind, whose defs live in the ancestor's database.
- A main-thread dispatcher that spreads a burst of requests across frames and
  records each request's main-thread time.
- The `advance_time` run lifecycle, driven by synthetic observations: completion at
  the target tick, interruption by an outside pause or another game, stops on
  changes during the run but not on the starting state, and severity order when
  several things happen in one frame. Observing a live game is a live-game test.
- `advance_time` retries recognised across recent runs, with matching arguments
  required for a reused request id.
- `advance_time` stopping when stored food drops below the Low food threshold
  during a run, but not when it was already low.
- Waiting stays bound to one run: `get_advance` answers for the requested run id,
  and a waiting request returns when that run ends, through the real bridge
  dispatch with the test thread acting as the main thread.
- An injected observation failure ends the run and pauses the game, while a pause by
  someone else does not trigger another pause.
- `get_advance` only defaults to a run from the loaded game.
- Load ids that change with every loaded game, and the router refusing a command
  whose `expectedLoadId` names an earlier load.
- Access groups on every tool, preset groups, migration from Allow changes, the
  router refusing disabled groups while the access endpoint stays reachable, and
  turning off Time ending and pausing a running advance.
- Cell rectangles bounded, clipped to the map and de-duplicated; rejected cells
  all counted but listed up to a cap; and request ids: a retry returns the first
  result whatever its outcome (alreadySatisfied and rejected too) without the command
  running again, so it cannot undo what a player did in between; a failed request
  keeps nothing; and a reused id with other arguments or on another map is refused
  before anything runs. Applying zones, designations and orders to a map is a
  live-game test.
- List query fields split on commas, with unknown defNames refused.
- Storage `accepts` by top-level category, leaving out categories the storage
  cannot hold, counting a category as partly or not accepted when a special filter that
  is off refuses some or all of it, naming the storage's and the building's own
  special-filter refusals that could apply to what it holds, and giving the hit point
  and quality ranges where settings and building overlap.
- Growing zones reading each plant's growth, nightly rest, and light from the game's
  own members rather than the fallbacks.
- Map view layers that leave out their rows when they mark no cell, with rows
  running north to south and a legend of only the symbols shown.
- `order_pawn` and `designate` offering exactly the kinds they handle, with `queue`,
  `x`, and `z` in the `order_pawn` schema and body, and orders that cannot be queued
  refused before anything changes.
- Placement rotations parsed by name, and blockers keeping their kind first and
  dropping empty fields.
- Food trends that wait for six hours of samples and run over the last three days.
- Bill states (active, done, suspended) worked out without changing the bill.
- Storage edits that keep special filters, such as allowing rotten food, as the player
  set them, and report a change to the accepted items even when their count is the same.
- Storage entries that change a special filter only when it is named, refuse ones the
  game or the building fixes, and say which special filters still refuse what an entry
  allows.
- Single-kind ingredients that need one kind with enough, while mixed ones add up.
- `repeatCount` in both summary and full bill serialization, independently of
  `targetCount`.
- Bill settings that the bill dialog cannot produce: non-finite or sub-minimum
  ingredient radii, and unpause thresholds at or above the target. Also checks that
  changing the target moves the unpause threshold as the dialog does.
- Pawn and work reads that leave uninitialized work settings untouched.
- Permanent game conditions without duration lookups.
- Medical care parsing that rejects numeric and comma-separated enum values.
- Real research progress and completion values when a research manager exists.

The ideology check calls the real private `Ideos()` enumerable shared by both
ideology endpoints. Full endpoint invocation would initialize `ModsConfig` and
native game systems, so expansion activation and endpoint integration remain
live-game tests. The populated research test bypasses `Game`'s native-engine
constructor, attaches a real `ResearchManager`, and seeds its actual progress
dictionary. It does not emulate research calculations.

Exit code 0 means all checks passed; 1 means a regression failed; 2 means setup
failed. To verify the tests detect regressions, run the same executable with a
saved pre-fix mod DLL as its second argument. The unmodified 0.5.5 DLL fails nine
checks and retains the passing colony-research control. The unmodified 0.5.6 DLL
fails the four checks added after it; the 0.5.6 fixes build fails the two
checks added with the performance pass.
