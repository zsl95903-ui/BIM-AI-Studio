[CmdletBinding()]
param(
    [string]$RevitVersion = '2027'
)

$ErrorActionPreference = 'Stop'
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw 'Close Revit before installing the add-in.'
}

$packageRoot = $PSScriptRoot
$source = Join-Path $packageRoot 'addin\RevitAiEngine'
$manifest = Join-Path $packageRoot 'addin\RevitAi.Engine.addin'
if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $manifest)) {
    throw 'The package does not contain the expected add-in payload.'
}

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$destination = Join-Path $addinsRoot 'RevitAiEngine'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $addinsRoot 'RevitAi.Engine.addin') -Force

Write-Host "Installed Revit AI Engine: $destination"
Write-Host "Manifest: $(Join-Path $addinsRoot 'RevitAi.Engine.addin')"
