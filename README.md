# RimMCP

Agentic intelligence for the Rim.

RimMCP is a RimWorld mod that connects MCP-compatible clients to a live colony. The mod runs inside RimWorld and hosts a token-protected HTTP endpoint on `127.0.0.1`. MCP clients can then use the bundled stdio proxy.

Supports RimWorld 1.6 and 1.7 unstable using the same mod DLL. The 1.7 engine's compatibility layer loads the assembly built against the 1.6 API; future unstable updates may require further testing.

Current MCP tools:

| Area | Tools | Use these for |
| --- | --- | --- |
| Colony overview | `get_game_context`, `get_colony_status` | Current map, time, paused state, selected game speed, storyteller/difficulty, loaded mods, colony dashboard, and top risks. |
| Mods and logs | `list_mods`, `get_mod`, `get_game_log` | Inspect loaded/installed mods, load order, loader warnings, settings panel names, and retained game logs with optional stack traces. |
| Game settings | `get_game_settings`, `set_game_settings` | Read or change storyteller/difficulty presets, background running, pause options, autosave interval, and master/music volume. Writes require **Allow changes**. |
| Pawns and work | `list_pawns`, `get_pawn`, `list_work`, `set_pawn_drafted`, `set_pawn_work_priority`, `set_pawn_assignment` | Inspect colonists, prisoners, animals, threats, work priorities, schedules, policies, medical care, allowed areas, and other Assign-tab settings. |
| Production | `list_production`, `get_bill`, `set_bill`, `list_workshops`, `get_workshop`, `add_bill_to_workshop` | Inspect bills and workbenches, add new bills, update bill settings, suspend bills, or delete bills. |
| Map state | `list_resources`, `list_zones`, `get_zone`, `list_buildings`, `get_building`, `get_environment`, `get_power`, `list_threats` | Inspect stockpiles, growing zones, buildings, storage contents, weather, rooms, power grids, hazards, fires, and hostile activity. |
| Animals | `designate_animal`, `set_animal_training` | Set hunt/tame/slaughter designations and colony animal training flags. |
| Research and ideology | `get_research`, `set_research_project`, `list_ideoligions`, `get_ideoligion` | Inspect or choose research, and inspect Ideology data when the expansion is active. |
| Time | `set_game_speed`, `advance_time`, `get_advance` | Pause or set the speed, or run the game for a set number of in-game hours and pause again. A run stops early on deaths, downed or injured colonists, mental breaks, hostiles, or threat letters, and reports what happened. Changes require **Allow changes**. |
| World and defs | `list_quests`, `list_world`, `search_defs`, `get_def` | Inspect quests, factions, world objects, and loaded definitions with their defining mod when known. |

Most tools accept the same optional read controls:

- `mapId`: select a specific loaded map; defaults to the current map.
- `detail`: `summary`, `normal`, or `full`; defaults to compact `summary`.
- `include`: request specific heavier sections without switching to full detail, such as animal `training`.
- `limit` and `cursor`: page large lists deterministically.
- `idsOnly`: return identifiers only when supported by the list tool.

Tool responses use a shared envelope:

```json
{
  "schemaVersion": "1",
  "tick": 12345,
  "mapId": "1",
  "truncated": false,
  "data": {}
}
```

The MCP proxy mirrors the structuredContent JSON in text content for clients that do not read structured tool results yet (**cough, cough** opencode).

## Using The Mod

1. Open the RimMCP mod settings page.
2. Click **Copy MCP config** to copy the MCP configuration including the URL and token.
3. Configure your preferred Agentic client with MCP support. (Most agents can configure themselves given the MCP config info)
4. **Allow changes** is on by default, including when upgrading from a version without this setting. Turn it off in RimMCP settings for read-only access. A saved off setting stays off.

If you want MCP to work while RimWorld is alt-tabbed or minimized, enable RimWorld's **Run in background** setting. Otherwise requests will timeout when the game is not in focus.

Current command HTTP endpoints:

- `PUT /v1/pawns/{pawnId}/drafted`
- `PUT /v1/pawns/{pawnId}/work/{workTypeDefName}`
- `PUT /v1/pawns/{pawnId}/assignments/{assignmentKind}`
- `PUT /v1/bills/{id}`
- `POST /v1/workshops/{workshopId}/bills`
- `PUT /v1/animals/{pawnId}/designation`
- `PUT /v1/animals/{pawnId}/training`
- `PUT /v1/research/current`
- `PUT /v1/time/speed`
- `POST /v1/time/advance`

## Building From Source

Requirements:

- RimWorld 1.6 reference DLLs, even when running RimWorld 1.7 (see below)
- .NET 8 SDK or newer
- Rust (the toolchain is pinned in `rust-toolchain.toml`)
- Python 3
- A native C/C++ toolchain.
- Linux requires `musl-tools`

On macOS with Homebrew:

```sh
brew install --cask dotnet-sdk
```

Build the RimWorld mod DLL against **RimWorld 1.6**. Set `RimWorldManagedPath` to its managed-assembly directory, or a retained copy containing `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, and `UnityEngine.IMGUIModule.dll` from the same 1.6 installation. On Windows/Linux the normal location is `RimWorld/RimWorldWin64_Data/Managed` or `RimWorld/Data/Managed`, depending on the installation; on macOS it is inside the app bundle:

```sh
dotnet build RiMCP/Source/RiMCP.csproj -c Release \
  -p:RimWorldManagedPath="/path/to/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed"
```

For PowerShell, pass the property as one quoted argument:

```powershell
dotnet build RiMCP/Source/RiMCP.csproj -c Release "-p:RimWorldManagedPath=D:\RimWorld-1.6\Data\Managed"
```

The output is `RiMCP/Assemblies/RiMCP.dll`, targeting .NET Framework 4.7.2 for RimWorld 1.6 and 1.7. The DLL version is read from `RiMCP/About/About.xml`.

RimWorld 1.7 puts its assemblies in the game root and uses .NET 10 with `UnityStubs.dll` in place of the Unity modules. Those are **runtime assemblies, not compatible build references** for this shared `net472` DLL. Keep the 1.6 references before switching branches, or obtain them from a separate 1.6 installation. The build checks the reference version and required modules and reports a specific error if the 1.7 directory is used. Do not distribute the game reference DLLs with the mod.

Build and publish the MCP proxy for the current platform:

```sh
python scripts/publish-mcp-proxy.py
```

The **Build MCP helpers** workflow builds all four platforms on native runners. Mac helpers are signed and notarized. Published executables are at:

- `RiMCP/Tools/McpProxy/win-x64/RiMCP.McpProxy.exe`
- `RiMCP/Tools/McpProxy/linux-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-arm64/RiMCP.McpProxy`

## License

RimMCP's original code and assets are licensed under the MIT License. See [LICENSE](LICENSE).

RimWorld, Ludeon Studios, and related names and assets are owned by Ludeon Studios. RimMCP is an unofficial community mod and is not endorsed by Ludeon Studios. Distribution and use as a RimWorld mod remains subject to the RimWorld EULA and any platform terms that apply, such as Steam Workshop terms.
