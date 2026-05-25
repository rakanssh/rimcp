# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Added

- Typed MCP command tools and PUT endpoints for pawn drafting, work priorities, schedules, prisoner interaction modes, animal designations, animal training, and current research selection.
- MCP tool annotations for read-only reads, idempotent commands, destructive commands, and closed-world bridge behavior.
- Animal training details in pawn reads when requested with full detail or `include=["training"]`.
- Request body size limit for command payloads.

### Changed

- Refactored bridge dispatch to a unified method-aware route table shared by read and command endpoints.
- Simplified MCP proxy endpoint definitions with method-aware metadata and templated paths.
- Moved map, pawn, and def resolution into shared command helpers where appropriate.

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
- Detail, include, pagination, map selection, not-modified, and ids-only controls for supported read tools.
- RimWorld mod settings flow for copying MCP client configuration.

[0.1.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.1.0
