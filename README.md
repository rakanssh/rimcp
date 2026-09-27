# RiMCP

Agentic intelligence for the Rim.

RiMCP is a RimWorld mod that connects MCP-compatible clients to a live colony. The mod runs inside RimWorld and hosts a token-protected HTTP endpoint on `127.0.0.1`. MCP clients can then use the bundled stdio proxy.

## Status

The current build exposes a read layer for colony inspection plus a small typed command surface for simple colony control.

Current MCP tools:

| Area | Tools | Use these for |
| --- | --- | --- |
| Colony overview | `get_game_context`, `get_colony_status` | Current map, time, storyteller/difficulty, loaded mods, colony dashboard, and top risks. |
| Pawns and work | `list_pawns`, `get_pawn`, `list_work`, `set_pawn_drafted`, `set_pawn_work_priority`, `set_pawn_assignment` | Inspect colonists, prisoners, animals, threats, work priorities, schedules, policies, medical care, allowed areas, and other Assign-tab settings. |
| Production | `list_production`, `get_bill`, `set_bill`, `list_workshops`, `get_workshop`, `add_bill_to_workshop` | Inspect bills and workbenches, add new bills, update bill settings, suspend bills, or delete bills. |
| Map state | `list_resources`, `list_zones`, `get_zone`, `list_buildings`, `get_building`, `get_environment`, `get_power`, `list_threats` | Inspect stockpiles, growing zones, buildings, storage contents, weather, rooms, power grids, hazards, fires, and hostile activity. |
| Animals | `designate_animal`, `set_animal_training` | Set hunt/tame/slaughter designations and colony animal training flags. |
| Research and ideology | `get_research`, `set_research_project`, `list_ideoligions`, `get_ideoligion` | Inspect or choose research, and inspect Ideology data when the expansion is active. |
| World and defs | `list_quests`, `list_world`, `search_defs`, `get_def` | Inspect quests, factions, world objects, and loaded RimWorld/mod definitions. |

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

The MCP proxy returns the full payload as `structuredContent` and also mirrors the JSON in text content for clients that do not read structured tool results yet (**cough, cough** opencode).

## Using The Mod

1. Open the RiMCP mod settings page.
2. Click **Copy MCP config** to copy the MCP configuration including the URL and token.
3. Configure your preferred Agentic client with MCP support. (Most agents can configure themselves given the MCP config info)
4. **Allow colony changes** is on by default, including when upgrading from a version without this setting. Turn it off in RiMCP settings for read-only access. A saved off setting stays off.

If you want MCP to work while RimWorld is alt-tabbed or minimized, enable RimWorld's own **Run in background** setting. Otherwise requests will timeout when the game is not in focus.

Command tools use the same identifiers returned by the read tools and report whether they changed game state. Most command tools are idempotent state setters. The bridge rejects command requests while **Allow colony changes** is off.

Current command HTTP endpoints:

- `PUT /v1/pawns/{pawnId}/drafted`
- `PUT /v1/pawns/{pawnId}/work/{workTypeDefName}`
- `PUT /v1/pawns/{pawnId}/assignments/{assignmentKind}`
- `PUT /v1/bills/{id}`
- `POST /v1/workshops/{workshopId}/bills`
- `PUT /v1/animals/{pawnId}/designation`
- `PUT /v1/animals/{pawnId}/training`
- `PUT /v1/research/current`

## Building From Source

Requirements:

- RimWorld 1.6
- .NET 8 SDK or newer

On macOS with Homebrew:

```sh
brew install --cask dotnet-sdk
```

Build the RimWorld mod DLL:

```sh
dotnet build RiMCP/Source/RiMCP.csproj -c Release \
  -p:RimWorldManagedPath="/path/to/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed"
```

Build and publish the bundled MCP proxy:

```sh
sh scripts/publish-mcp-proxy.sh
```

This creates self-contained proxy executables for:

- `RiMCP/Tools/McpProxy/win-x64/RiMCP.McpProxy.exe`
- `RiMCP/Tools/McpProxy/linux-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-arm64/RiMCP.McpProxy`

The mod settings copy MCP config that launches `RiMCP/Tools/McpProxy/rimcp-proxy.cmd` on Windows and `RiMCP/Tools/McpProxy/rimcp-proxy` on macOS/Linux. Those launchers select the bundled executable for the current platform.

For local testing, copy or symlink the `RiMCP/` folder into RimWorld's `Mods/` directory.

After building the mod and publishing all four proxies, run `python scripts/package-release.py` (Python 3). It reads the version from `About.xml` and creates a clean Workshop folder at `.dotnet-home/releases/v<version>/RiMCP/`, plus a ZIP and SHA-256 checksum beside it. The package includes the artwork, runtime license notices, and an existing `About/PublishedFileId.txt` for Workshop updates. Existing output directories are never overwritten; use `--output-dir <new-directory>` to package the same version again.

## License

RiMCP's original code and assets are licensed under the MIT License. See [LICENSE](LICENSE).

RimWorld, Ludeon Studios, and related names and assets are owned by Ludeon Studios. RiMCP is an unofficial community mod and is not endorsed by Ludeon Studios. Distribution and use as a RimWorld mod remains subject to the RimWorld EULA and any platform terms that apply, such as Steam Workshop terms.
