# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Fixed

- Simplify the pawn-assignment tool schema so MCP clients can discover all arguments.

### Added

- An **Allow colony changes** setting, enabled by default, that can block commands while keeping reads available. Existing configurations without this setting also default to enabled; a saved off setting stays off.
- Workshop description draft and final 1280 × 720 preview artwork with editable SVG source.

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

[Unreleased]: https://github.com/rakanssh/rimcp/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.4.0
[0.3.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.3.0
[0.2.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.2.0
[0.1.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.1.0
