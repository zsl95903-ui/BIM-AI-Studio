# Changelog

## Unreleased

- Added an application-to-toolset compatibility layer using `IAIToolRegistry`,
  `AIToolInvocationHandler` and `RevitToolHost`.
- Unified the maintained Revit 2027 UI in `BIM-AI-Studio-App/host/hybrid-ui`.
- Renamed the tool-layer directory to `toolset` and aligned build paths across
  both repositories.
- Consolidated tool execution on the project-owned registry and transaction
  host.
- Updated documentation to distinguish automated verification from live Revit
  validation.
- Documented the limited maintenance capacity of an individual developer and
  the features that still require representative project testing.

## 2.0.0 - 2026-10-21

- Published the Apache-2.0 toolset source, contracts, tests and build scripts.
- Added 120 contract tools and the `upsert_level` extension.
- Added Revit registry discovery, external-event routing, transaction handling
  and a 355-item smoke suite.
- Added the Revit 2027 bridge, MCP server and Agent invocation scripts.
- Added reproducible build and packaging workflows.

## 1.0.0 - 2026-10-17

- Introduced the BIM-AI Studio project structure.
- Added the typed Revit bridge and tool catalog.
- Added MCP forwarding, Agent scripts and tool-authoring documentation.
- Established the Apache-2.0 source and distribution boundaries.
