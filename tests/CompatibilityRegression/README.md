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

The ten checks cover:

- Main-menu research listing, definition lookup, and definition search, including
  absence of invented `finished`/`progress` values.
- Empty quests, ideology enumeration, and world data without a loaded game.
- A controlled 409 research-command rejection without a game.
- A numeric cursor roundtrip through actual endpoint JSON, including distinct
  subsequent pages and termination on the last page.
- `repeatCount` in both summary and full bill serialization, independently of
  `targetCount`.
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
checks and retains the passing colony-research control.
