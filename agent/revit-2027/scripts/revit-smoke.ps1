[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$client = Join-Path $PSScriptRoot "revit-ai.ps1"

$ping = & $client ping -TimeoutSeconds $TimeoutSeconds | ConvertFrom-Json
if ($ping.success -ne $true) {
    throw "Revit bridge is reachable but ping failed."
}

$catalog = & $client tools -TimeoutSeconds $TimeoutSeconds | ConvertFrom-Json
if ($catalog.success -ne $true -or [int]$catalog.count -lt 1) {
    throw "Revit bridge is reachable but returned no tools."
}

$levels = & $client invoke get_all_levels "{}" -TimeoutSeconds $TimeoutSeconds | ConvertFrom-Json
if ($levels.success -ne $true) {
    throw "Revit bridge is reachable but get_all_levels failed: $($levels.errorMessage)"
}

$levelPayload = if ($null -ne $levels.payload.data) { $levels.payload.data } else { $levels.payload }
[PSCustomObject]@{
    success = $true
    toolCount = [int]$catalog.count
    levelCount = [int]$levelPayload.count
    levels = $levelPayload.levels
} | ConvertTo-Json -Depth 20