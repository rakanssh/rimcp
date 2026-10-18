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

The seventy-seven checks cover:

- Main-menu research listing, definition lookup, and definition search, including
  absence of invented `finished`/`progress` values and blockers, and `availableOnly`
  refused without a colony.
- Empty quests, ideology enumeration, and world data without a loaded game; quest tabs
  other than available, active and historical refused; and events refused without a game.
- History paging on entries made in the test: the latest entries without a cursor, then
  every new entry exactly once across pages, including a quest letter numbered before
  others but arriving later and a letter arriving at the cursor's own tick; a message the
  game repeats reported once as repeated; a cursor from another load starting over; and
  gaps flagged from recorded losses after the cursor, but not before it.
- The History watch on the game's own `Archive`, which culls as entries arrive: an arrival
  that culls an entry already read is seen go without a gap; more arrivals between two looks
  than the archive keeps, one culled before the watch saw it, flag a gap for a reader caught
  up before them (and only because of the unseen one); and a reader caught up after them is
  not told again while nothing changes.
- Dialog answers on a scripted dialog: a new page for each question with numbers never
  reused, an answer naming an earlier page refused on a later one with the same buttons,
  changed, disabled, counting-down and covered options refused before anything is chosen,
  and the player answering or closing first, cancel where allowed, and revoked access
  ending the interaction.
- A count dialog taking a value only within its range, and other dialogs taking none.
- A player's answer that leaves a dialog open ending the agent's part, the agent's own answer
  not mistaken for the player's, and changed options making a new page to read.
- Dialog answers keeping their receipts after the dialog is no longer kept, and a mod's dialog
  refused while mods' content is off.
- An answer to a page whose options shrank before it arrived refused with a controlled 409
  before the answer's code is looked up, choosing nothing.
- A press that fails after starting targeting: the targeting cancelled, the failure reaching the
  caller, and the dialog settled on a new page so the answer is not taken for the player's; and
  no press run, or targeting touched, while the player is choosing a target. Targeting is
  simulated through the replaceable check and cancel, since the harness has no targeter.
- Letter revisions differing between loaded games.
- Every right-click menu provider and every kind of button in the game present in the
  audit, and the audit naming none the game lacks.
- Options and buttons counted as the game's own only when their code is: a game provider
  running a mod's code, a button with a mod's callback, and a button whose own press handling
  is a mod's are mod content; trading and designator buttons are refused.
- The audit tables naming launching, royal permits, comms calls run in the faction or ship called,
  and bestowing ceremonies, and a mod's verb inside a stock button counted as mod content.
- Composed game buttons and options refused by the code they run: ability parts that open the
  ritual window (a subclass by its base type), an action button running a sealable's code, one
  running a bed's owner code (by its base type's entry), and a thing's option running a
  faction's code (a comms call); an ordinary game button is not refused. A `Command_Ability`
  cannot be made outside the game, since its static fields load textures, so its ability is
  given to the button audit apart.
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
- Single tools turned on or off as exceptions to their group: dropped when they agree
  with it, a group flipping when all its tools do, presets recognised again, and the
  access endpoint and the router following them.
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
- Session operations: one at a time, one step per frame, cancelled or revoked only at a
  safe step and after any native step, with the last 32 finished operations kept.
  Retries return the original operation, or an expired answer once its details are
  evicted, for the last 1,000 requests.
- Replacing a loaded colony only with a save first or an explicit discard, and
  commitment mode keeping the game's rules.
- Save verification: a file written by this save, complete, and plain XML with its
  header; the log watch counting errors on the saving thread and seeing its marker;
  and save headers giving version, mods (matched as the game checks) and commitment
  mode.
- Only operation status answering while a game is replaced, and cancelling an
  operation needing its own group.
- Starting sites filtered on a tile's own facts (biome, hilliness, temperature and
  rainfall bounds including their ends, river and road, never water), longest growing
  period first, then the temperature nearest the range asked for.
- Setup commands refused without an open setup, without `expectedRevision`, or with
  a stale one, changing nothing; and `answer_confirmation` refused in visible setup
  and for any question but the one asked, and answered only once. Driving the setup screens
  is a live-game test.
- Settlement distances bounding the nearest counted settlement, with a maximum needing one;
  and custom difficulty settings rounding to their steps and reading and writing as the
  panel shows them (the reciprocal disease interval, fixed wealth time in twelfths, inverted
  checkboxes), checked on a copy.
- Ideoligion names, adjectives, member names and precept names limited to the dialogs'
  lengths and characters, and precept defs sorted into the custom ideoligion screen's
  sections by class.

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
