# Revit 2027 Modeling Recipes

All examples use the live catalog and live schema. IDs shown below are illustrative from one test document and are not stable.

## 1. Connection

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 tools
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 schema create_straight_wall
```

## 2. Resolve Context

Read levels:

```powershell
.\scripts\revit-ai.ps1 invoke get_all_levels '{}'
```

Read model categories:

```powershell
.\scripts\revit-ai.ps1 invoke get_all_elements '{}'
```

`get_all_elements` can be expensive on large models. Prefer `element_query` with a category or ID when only part of the model is needed.

Read family types:

```powershell
.\scripts\revit-ai.ps1 invoke get_all_families '{}'
.\scripts\revit-ai.ps1 invoke get_family_types '{"familyName":"墙"}'
```

Use the returned `typeId` as `wall_type_id`, `floor_type_id`, `door_type_id`, and so on.

## 3. Create Levels

`create_level.elevation` is in metres.

```powershell
.\scripts\revit-ai.ps1 invoke create_level '{"levelName":"L03","elevation":9.0}'
```

For batch creation, pass matching arrays.

## 4. Create A Precision Wall

`create_straight_wall` expects millimetres and a valid level/type ID.

1. Read levels and wall types.
2. Select the exact level and wall type.
3. Create the wall.
4. Read back its location and geometry.

```powershell
.\scripts\revit-ai.ps1 invoke create_straight_wall '{"start_x":0,"start_y":0,"end_x":6000,"end_y":0,"level_id":311,"height":3000,"wall_type_id":398}'
.\scripts\revit-ai.ps1 invoke get_element_location '{"element_id":339672}'
.\scripts\revit-ai.ps1 invoke get_element_geometry '{"elementId":339672}'
```

If the creation result contains `wall_id`, use that value immediately for readback.

## 5. Create A Floor

`create_floor_by_profile` expects a closed sequence of at least three points in millimetres and a valid floor type.

```powershell
.\scripts\revit-ai.ps1 invoke create_floor_by_profile '{"points":[{"x":0,"y":0},{"x":6000,"y":0},{"x":6000,"y":4000},{"x":0,"y":4000}],"level_id":311,"floor_type_id":12345}'
```

Verify result IDs with `element_query`, `get_element_location`, or `get_element_geometry`.

## 6. Create Rooms

Create rooms only after the bounding geometry is closed and verified.

```powershell
.\scripts\revit-ai.ps1 invoke create_room '{"levelId":311,"x":3000,"y":2000,"roomName":"Studio","roomNumber":"101"}'
.\scripts\revit-ai.ps1 invoke get_all_rooms '{}'
.\scripts\revit-ai.ps1 invoke get_room_boundaries '{"roomId":123456}'
```

## 7. Modify And Verify

Read and modify parameters:

```powershell
.\scripts\revit-ai.ps1 invoke query_parameter_info '{"elementId":339672}'
.\scripts\revit-ai.ps1 invoke set_parameter_values '{"elementId":339672,"parameterName":"Comments","value":"Codex verified"}'
```

Move elements in millimetres:

```powershell
.\scripts\revit-ai.ps1 invoke move_elements '{"elementId":339672,"x":1000,"y":0,"z":0}'
```

Always query the result after a mutation.

## 8. Precision Building Sequence

For a rectangular building shell:

1. Query levels and choose or create the working level.
2. Query wall and floor types.
3. Create the four bounding walls with explicit endpoints in millimetres.
4. Read back all wall IDs and verify start/end coordinates.
5. Create the floor profile using the same coordinate system.
6. Read back floor geometry.
7. Create rooms and verify boundaries.
8. Add hosted doors/windows using the verified wall IDs and host-relative insertion points.
9. Create views, sheets, schedules, or exports only after geometry readback succeeds.
10. Do not save or sync unless the user explicitly requests it.

## 9. Failure Handling

- `tool_not_found`: run `tools` and use the exact live name.
- `revit_tool_busy`: wait for the current AS.Tools request to finish and retry.
- `revit_context_not_ready`: open the AI Console once and retry.
- Type mismatch or missing parameter: run `schema <tool>` and correct the JSON.
- Ambiguous or stale element ID: query the category or active selection again.
- Any partially applied multi-step operation: stop, read back all affected IDs, and report what changed before attempting cleanup.