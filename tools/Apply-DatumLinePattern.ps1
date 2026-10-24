[CmdletBinding()]
param(
    [string]$PatternName = "",
    [long]$PatternId = 0
)

$ErrorActionPreference = "Stop"

# Keep this script ASCII-only so Authenticode signing cannot corrupt its
# encoding. U+957F U+865A U+7EBF is the default Chinese pattern name.
if ([string]::IsNullOrWhiteSpace($PatternName)) {
    $PatternName = [string]::Concat(
        [char]0x957F,
        [char]0x865A,
        [char]0x7EBF)
}

$arguments = if ($PatternId -gt 0) { @{ patternId = $PatternId } } else { @{ patternName = $PatternName } }
$client = Join-Path (Split-Path -Parent $PSScriptRoot) "agent\revit-2027\scripts\revit-ai.ps1"
& $client invoke set_level_line_pattern ($arguments | ConvertTo-Json -Compress)
if ($LASTEXITCODE -ne 0) { throw "Datum line pattern update failed." }
