---
name: revit-2027
description: Control the locally running Autodesk Revit 2027 session through the installed Revit AI named-pipe bridge for live inspection, millimetre-precise modeling, preflight planning, and verified model changes.
---

# Revit 2027 Control

Use this skill when the user asks Codex to inspect or modify the currently open Autodesk Revit 2027 document.

## Connection And Discovery

- Run `scripts/revit-smoke.ps1` first. It verifies the named pipe, discovers the live tool catalog, and reads the current levels without modifying the model.
- The client checks `%LOCALAPPDATA%\RevitAi\bridge.json` and `%LOCALAPPDATA%\ReVitAI\revitai-bridge.json`.
- Keep the BIM-AI Studio application add-in enabled.
- Use `scripts/revit-ai.ps1 tools` for the authoritative live catalog.
- For structured engineering drawing, use `apply_drawing_plan` with a root `viewName` or fresh `viewId`, `activateView: true`, and `autoZoom: true`.
- Prefer `create_detail_curves` for lines, center arcs, three-point arcs, and polylines; prefer `create_text_notes` for recognized PDF/CAD text.
- Use `scripts/revit-ai.ps1 schema <tool>` before constructing arguments.
- If the catalog is empty or reports `revit_context_not_ready`, ask the user to open `Revit AI > AI Console` once in Revit, then retry.
- Use `scripts/revit-ai.ps1 agent-status` to inspect the plugin's internal natural-language Agent configuration and tool availability.
- Use `scripts/revit-ai.ps1 chat "<instruction>"` only when the user explicitly wants the plugin's built-in Agent to plan and execute. For deterministic work, prefer direct `schema` and `invoke` calls.
- When delegating to the built-in Agent, include exact dimensions, existing-element checks, fallback authorization, readback requirements, and the no-save boundary in the instruction.
- Do not hard-code element IDs, level IDs, or family-type IDs. Resolve them from the active document.
- `references/revit-2027-tool-catalog.json` is a dated snapshot for orientation only. The live catalog always wins.

## Model Changes

- Apply a mutation only when the user requested that change. Never save, synchronize, close, or overwrite a document unless the user explicitly asks.
- If `supportsDryRun` is true, call the tool with `dry_run: true` first.
- Many installed AS.Tools operations do not support dry-run. For those, perform a read-only preflight: resolve target IDs, level IDs, type IDs, coordinates, and impact scope; present the exact plan; then invoke once the user has authorized the write.
- Keep each call scoped to one logical operation. Do not hide a multi-building or mass edit inside one ambiguous request.
- After every write, query the affected elements back. Report changed IDs, requested values, observed values, and any uncertainty.
- Do not call `execute_code` or equivalent unrestricted code execution unless the user explicitly authorizes code execution for the current task.
- Do not claim success from a plan, message, or returned text alone. Require an independent readback.

## Precision Modeling

- Prefer the units specified by each live tool schema. Most AS.Tools geometry tools use millimetres; `create_level.elevation` uses metres; `get_all_levels` returns metres in `elevation`; `get_element_location` returns millimetres.
- Resolve family/type IDs with the live catalog before creating walls, floors, doors, windows, beams, columns, pipes, ducts, or family instances.
- Build in dependency order: levels, grids, walls/floors, hosted elements, MEP/structural elements, annotations, views and sheets.
- For closed architectural spaces, create and verify bounding walls/floor first, then create rooms and verify room boundaries.
- Use `get_element_geometry` when full extent, length, area, or volume matters. `get_element_location` is only a point or curve location summary.
- Treat IDs returned by a create operation as ephemeral until readback confirms they still resolve.

Read `references/modeling-recipes.md` for concrete workflows. Read `references/revit-bridge.md` for transport and error handling.
