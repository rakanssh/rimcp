# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Added

- Sessions: `save_game`, `load_game` and `leave_game` run as operations that outlive the game they replace, followed with `get_operation` and stopped with `cancel_operation`. A save counts only once verified, because the game hides its own save errors: the file is complete, and nothing was logged as an error while it saved. Replacing a loaded colony always needs a save first or an explicit discard, and commitment mode keeps the game's own save rules.
- `list_saves` lists saves with their game version, whether their mods match the running ones, and commitment mode.
- New games: `start_setup` opens the game's own setup screens for a scenario from `list_scenarios`, and `set_setup_storyteller`, `generate_world`, `choose_starting_site`, `set_setup_ideoligion` and `set_starting_pawns` fill them in as a player would, alongside `get_setup`, `setup_back` and `cancel_setup`. `find_starting_sites` searches the world by biome, hilliness, river, road, coast, temperature, rainfall and growing period, a few milliseconds a frame. `set_starting_pawns` rerolls colonists until they meet skill, passion, trait, work and age criteria, and names them. Setup stays on screen: every change takes the revision the agent last read, a change by the player stops the agent's next step, and `start_game` asks the player to approve the setup. Screens RimMCP does not drive, such as custom ideoligions, are left to the player.
- More of the setup screens: custom difficulty, setting each control of the screen's custom panel within its own range and steps; site search by distance to other factions' settlements (counting allies, neutrals or hostiles), leaving out tiles where a faction would lose goodwill, with each site's nearest settlement; and on the colonist screen, rearranging colonists, choosing their age and xenotype (Biotech), and rerolling for a xenotype.
- Custom ideoligions for new games: `set_setup_ideoligion` creates a custom fixed or fluid ideoligion from a structure and memes, through the game's meme dialogs, or loads a saved one. `edit_setup_ideoligion` then changes its memes, culture, styles, name, and precepts in every section (by issue, roles, rituals, buildings, relics, weapons, venerated animals, preferred xenotypes and apparel): adding what the screen's Add buttons offer, and replacing, regenerating, renaming or removing what each precept's box allows. Every refusal gives the game's reason, such as memes that exclude each other or a precept a meme requires, and `done` leaves the screen only when its own checks pass.
- An Unattended game setup setting, off by default, lets agents answer the game's questions with `answer_confirmation` and start the colony without the player's approval.
- A Game setup access group for saving, loading, leaving and setting up games, on only under Full control.
- `get_events` reads what the game is telling the player: active alerts with the pawns, things or places they concern, letters waiting with a choice letter's options, and the History tab's letters and messages. History pages from where the agent last read, without gaps or duplicates; a message the game repeats comes back once as repeated, a cursor from another load starts over, and entries the game removed before they were read are reported as a gap, as are arrivals that may have been culled before RimMCP saw them. Letters agents cannot answer, such as a child's growth moment, say so and are left to the player.
- Decisions: `answer_letter` answers a letter through its own dialog, and `respond_to_quest` accepts a quest offer as the Quests tab does (for a chosen reward, with a colonist who meets its requirements) or dismisses or restores a quest. They need no approval, as ordinary play, and show in game. A new Decisions access group holds them, on under Full control and Control.
- `answer_dialog` answers the question an agent's call leads to, such as a letter's next page or an "are you sure?" box. Each question is a page: an answer names the page it read and is refused once that page is gone, so it cannot answer a later question with the same buttons. A question whose options change becomes a new page to read. The dialog stays on screen, and the player answering it first (even with a button that leaves it open), closing it, turning the tool off, or loading another game ends the agent's part. Answers follow the same rules as the call that opened the dialog. A dialog opening ends a running `advance_time`, and a new one waits until the dialog is answered.
- `assign_owner` assigns pawns to beds, thrones and other buildings with owners, or unassigns them, with the Assign dialog's checks.
- The right-click menu: `list_pawn_actions` lists the menu a pawn gets on a thing or cell as the game builds it, and `choose_pawn_action` chooses an option as a click does, so any order the menu offers works, such as capture, arrest, strip, or prioritize. Choosing builds the menu again and is refused if it changed since it was read.
- Buttons: `list_gizmos` lists the buttons a building or pawn shows, `set_building_toggle` sets the game's building toggles (power switches, doors held open, medical beds, hold fire), and `use_gizmo` presses any other button, giving a target button its target directly, checked with the button's own rules: a mortar's attack, an ability, or a two-step ability's destination.
- The game's own options and buttons were audited for 1.6 and 1.7: flows agents cannot finish, such as trading, rituals, the comms console, or anything asking for a target RimMCP cannot check first, are refused before anything runs. A count asked for along the way, such as how many to pick up, is answered with `answer_dialog`.
- Mods' options and buttons work too, marked with their mod: they run as a click would, a target they ask for is checked against the mod's own rules before it is given, and otherwise their targeting is cancelled as Escape does. Content counts as a mod's when any code it runs is, a mod's verb or ability inside a game button included. A Safety setting, Mods' menu options and buttons, on by default, can turn them off, also for dialogs they already opened.
- Every press goes through one set of rules: menu options, buttons, letter options and dialog answers alike wait while the player is choosing a target, act on their own thing as if it were selected, through any targeting they start, without touching the player's selection, map or camera, leave no targeting behind when they fail, and are bound to the loaded game, so an option read before loading another game is refused.

### Changed

- `list_quests` lists quests as the Quests tab does, leaving out hidden ones, by tab (available, active, historical), with dismissed quests marked. Offers say whether they can be accepted and why not, when they expire, their reward choices, and which colonists can accept them; accepted quests give their accepter and the reward chosen.
- The settings are now in tabs (Connection, Access, Activity, Safety) under a master switch, Agents on, and open on Connection until an agent has connected. The Access tab browses every tool by group, with search, and turns single tools on or off on top of the access level; tools added in later versions follow their group.
- Agents see only the tools that are on: the helper lists those and tells the client when they change.
- A RimMCP button on the bottom bar shows whether agents are on and working, and pauses them or changes the access level in a click.
- The Activity tab counts calls per tool.
- Tool descriptions and parameters are about a fifth shorter, so agents load less: repeated wording is shared or trimmed, and tools no longer list options they do not read.
- While RimWorld loads, saves or changes games, requests other than operation status get a 503 instead of reading a half-built game. Saving first before leaving, loading or starting a new game pauses the game and holds it unchanged until it is replaced, so nothing done after the save is lost.

## [1.0.0] - 2026-10-17

### Added

- Colony control, through the game's own designators, right-click menus, and placement rules, so every in-game check applies and refusals give the game's reason:
  - Storage and zones: `set_zone` (stockpiles and growing zones), `set_storage` (priority, categories, things, and special filters such as AllowRotten), `set_area` (Home and allowed areas), and `set_growing_zone`.
  - Work orders: `designate` (forbid, haul, cut, harvest, chop, mine, deconstruct, cancel), and `check_placement` and `place_blueprint` for building.
  - Pawn orders: `order_pawn` to equip, wear, move, attack, rescue, or tend, replacing the current job or queued like a shift-click.
- Time control: `set_game_speed`, and `advance_time`/`get_advance` to run the game for 0.01 to 24 in-game hours, then pause, stopping early when a colonist dies, goes down, is hurt, or has a mental break, or on hostiles, low food, or a threat letter.
- Finding things: `find_things`, and `get_map_view`, a text map with colonists by letter, other pawns, terrain, and roof and designation layers.
- Blockers that say why something will not happen: bills (ingredients, workers, fuel, power), growing zones (season, sowable cells, growers, and why sown plants are not growing), blueprints (materials, skill), and research (no bench or researcher).
- The dashboard tracks food: stored meals and raw nutrition, daily use, days left, what rots soon, and the trend, with risks for hungry pawns, blocked bills, and rotting food.
- Safe retries: commands that create or order something take `requestId`, and a retry returns the first result without acting again, so it never undoes what a player did in between.

### Changed

- Access presets (Full control, Control, Administration, Read only) and per-group switches replace Allow changes, which carries over. Turning RimMCP or a group off applies at once, also to requests still waiting and a running advance, and writes and time control are refused during Multiplayer sessions.
- Stockpiles report what they accept, by category and including special filters, building limits, and quality and hit point ranges, instead of an ingredient summary.
- Power is reported in watts (`netPowerW`), and stored food uses the game's Low food count (`storedHumanNutrition`).
- The proxy runs tool calls concurrently, honours cancellation, and fails at once when RimWorld is in the background with Run in background off.
- Def search and large reads cost far less main-thread time, bursts of requests are spread across frames, and the settings log shows each request's main-thread time.

### Fixed

- Commands follow the game's own checks for drafting, prisoner modes (Convert now works), work priorities, animal designations, and bills.
- Reads no longer log errors, modify the save, or reveal anything under fog, such as an unopened ancient shrine's sleepers and loot.
- `get_environment` lists real rooms only, without doorways or the outdoors.
- `search_defs` finds modded def subclasses, and `list_work` lists every active work type.

## [0.5.6] - 2026-10-13

### Changed

- Declare RimWorld 1.7 support while retaining the shared .NET Framework 4.7.2 build for RimWorld 1.6 and 1.7.

### Fixed

- Read research definitions, quests, ideologies, and world context at the main menu without dereferencing a missing game or world. Research progress and completion are omitted when no colony is loaded.
- Return a controlled error when setting research without a loaded colony.
- Include the remaining repeat count in production bill reads.
- Return numeric pagination cursors matching the MCP tool input schema.
- Respond to MCP `ping` requests without contacting the game bridge.

## [0.5.5] - 2026-10-11

### Changed

- switched proxy to rust, package size down to 8mb 🦀

## [0.5.4] - 2026-10-11

### Changed

- reduced mod size by trimming dependencies

## [0.5.3] - 2026-10-10

### Added

- Mod inspection with loaded and installed inventories, load order, loader warnings, dependencies, assemblies, and settings panel discovery.
- Recent in-game log reads with severity/search filters and optional stack traces.
- Game settings tools for storyteller/difficulty presets, background running, pause options, autosave interval, and master/music volume.
- Defining mod information in definition search and detail results.
- Paused state and selected game speed in game context.
- A manual GitHub Actions workflow to sign and notarize both macOS helpers using a Developer ID Application certificate.

### Changed

- Rename the mod to **RimMCP** across the artwork, descriptions, settings, and messages.
- Rename the access toggle to **Allow changes** to cover colony commands and global game settings.

### Fixed

- Return game context at the main menu without requiring a loaded world.

## [0.5.2] - 2026-09-28

### Changed

- Rename the public mod to **Rim-MCP**, including its artwork, descriptions, settings, and messages. Existing package IDs, MCP configuration, and internal paths stay unchanged.

## [0.5.1] - 2026-09-28

### Changed

- Use the full Workshop description in the in-game mod page, with plain headings and lists that render in RimWorld.

## [0.5.0] - 2026-09-28

### Fixed

- Simplify the pawn-assignment tool schema so MCP clients can discover all arguments.
- Bundle native .NET runtime libraries and their license notices with the connection helpers.
- Restore the Unix helper's executable permission when it is missing after installation.

### Added

- An **Allow colony changes** setting, enabled by default, that can block commands while keeping reads available. Existing configurations without this setting also default to enabled; a saved off setting stays off.
- Workshop description and final 1280 × 720 preview artwork with editable SVG source.
- Clean release packaging with a Workshop folder, ZIP archive, and SHA-256 checksum.

### Changed

- Settings labels and bridge health responses now distinguish read-only access from enabled colony commands.

## [0.4.0] - 2026-09-27

### Fixed

- Reject target-count bills for recipes whose product counter cannot support them.
- Contain HTTP client disconnect and listener shutdown errors within the bridge request handler.
- Calculate calendar fields from absolute game ticks while preserving elapsed `ticksGame` values.
- Return protocol errors for malformed MCP request envelopes without terminating the proxy.

### Added

- Production command tools and endpoints: `set_bill` (update/delete) and `add_bill_to_workshop` (add).
- Request queue limits and rate limiting (HTTP 429) to the main thread dispatcher under heavy load.
- Multi-platform packaging and launcher scripts to support native proxy execution on Windows, Linux, and macOS (arm64/x64).

### Changed

- Paginate threats before constructing response objects and count threat categories in a single pass.
- Increased the main thread dispatch wait timeout to 10 seconds.
- Downgraded the MCP proxy target framework from .NET 10.0 to .NET 8.0.
- Updated the mod's configuration generator to delegate execution to the platform-specific launcher scripts.
- Replaced duplicated bridge route and MCP tool catalogs with a shared manifest and `/v1/tools` introspection endpoint.
- Made def search mod-aware by discovering loaded def subclasses and accepting arbitrary def kind names.
- Made production bill repeat/store mode settings def-driven, including stockpile targets for `SpecificStockpile`.
- Consolidated repeated summary DTO construction for pawn, thing, building, workshop, bill, and faction responses.
- Hardened JSON serialization so non-finite numeric values emit `null` and unsupported DTO values fail explicitly instead of falling back to `ToString()`.

## [0.3.0] - 2026-05-26

### Added

- Typed MCP command tools and PUT endpoints for pawn drafting, work priorities, Assign-style pawn settings, animal designations, animal training, and current research selection.
- Ideoligion read tools for listing and inspecting Ideology beliefs when the expansion is active.
- MCP tool annotations for read-only reads, idempotent commands, destructive commands, and closed-world bridge behavior.
- Pawn assignment, ideoligion identity, and animal training details in pawn reads when requested with full detail or targeted `include` sections.
- Request body size limit for command payloads.
- MIT license, contributor licensing note, and RimWorld/Ludeon EULA notice.

### Changed

- Refactored bridge dispatch to a unified method-aware route table shared by read and command endpoints.
- Simplified MCP proxy endpoint definitions with method-aware metadata and templated paths.
- Moved map, pawn, and def resolution into shared command helpers where appropriate.
- Removed `sinceTick`/`notModified` read controls so reads always return current game state.
- Reduced allocation work in zone and resource reads by avoiding unnecessary cell/resource rescans.
- Removed the hard-coded local RimWorld managed DLL path from the mod project.

## [0.2.0] - 2026-05-25

### Added

- HTTP read endpoints for production workshops and available bills
- MCP tools for production workshops and available bills

## [0.1.0] - 2026-05-25

### Added

- Initial RimWorld mod bridge exposing a token-protected local HTTP endpoint.
- Bundled MCP stdio proxy for MCP-compatible clients.
- Shared read response envelope with schema version, tick, map id, truncation metadata, and structured data payloads.
- Core colony inspection tools: game context, colony status, pawns, resources, work, production, bills, zones, environment, power, research, quests, world, threats, buildings, and defs.
- Detail, include, pagination, map selection, and ids-only controls for supported read tools.
- RimWorld mod settings flow for copying MCP client configuration.

[0.5.4]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.4
[0.5.3]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.3
[0.5.2]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.2
[0.5.1]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.1
[0.5.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.0
[0.4.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.4.0
[0.3.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.3.0
[0.2.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.2.0
[0.1.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.1.0
