[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$workspace = Split-Path -Parent $root

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspace 'outputs'
}

if (-not $SkipBuild) {
    & (Join-Path $root 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Build failed; package creation stopped.'
    }
}

$packageName = "BIM-AI-Studio-Rebuilt-120-v$Version"
$packageRoot = Join-Path $OutputDirectory $packageName
$zipPath = "$packageRoot.zip"
$checksumPath = Join-Path $OutputDirectory "$packageName.sha256"
$outputFull = [IO.Path]::GetFullPath($OutputDirectory)
$packageFull = [IO.Path]::GetFullPath($packageRoot)
if (-not $packageFull.StartsWith($outputFull.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a package path outside the output directory.'
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

$addinRoot = Join-Path $packageRoot 'addin'
$payloadRoot = Join-Path $addinRoot 'RevitAiEngine.PoC'
$toolsRoot = Join-Path $packageRoot 'tools'
$docsRoot = Join-Path $packageRoot 'docs'
$sourceRoot = Join-Path $packageRoot 'source'
New-Item -ItemType Directory -Force -Path $payloadRoot, $toolsRoot, $docsRoot, $sourceRoot | Out-Null

$binaryRoot = Join-Path $env:TEMP 'revitai-engine-poc\bin\RevitAi.Engine.Addin\Release'
if (-not (Test-Path -LiteralPath $binaryRoot)) {
    throw "Build output not found: $binaryRoot"
}

Get-ChildItem -LiteralPath $binaryRoot -File |
    Where-Object { $_.Extension -in '.dll', '.json', '.pdb', '.addin' } |
    Copy-Item -Destination $payloadRoot -Force

Copy-Item -LiteralPath (Join-Path $binaryRoot 'RevitAi.Engine.addin') -Destination $addinRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'package-install.ps1') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'contracts\tool-contracts.json') -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $env:TEMP 'revitai-engine-poc\generated\tool-index.json') -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $env:TEMP 'revitai-engine-poc\generated\工具索引.md') -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $docsRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'NOTICE.md') -Destination $docsRoot -Force
Copy-Item -LiteralPath (Join-Path $workspace 'THIRD_PARTY_NOTICES.md') -Destination $docsRoot -Force
$finalReport = Join-Path $OutputDirectory 'full-rebuild-report.md'
if (Test-Path -LiteralPath $finalReport) {
    Copy-Item -LiteralPath $finalReport -Destination $docsRoot -Force
}
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $packageRoot -Force

Copy-Item -Path $root -Destination (Join-Path $sourceRoot 'clean-room-tools') -Recurse -Force
$copiedSource = Join-Path $sourceRoot 'clean-room-tools'
Get-ChildItem -LiteralPath $copiedSource -Recurse -Directory -Force |
    Where-Object { $_.Name -in '.git', 'bin', 'obj', 'artifacts' } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

$contracts = Get-Content -LiteralPath (Join-Path $toolsRoot 'tool-contracts.json') -Raw | ConvertFrom-Json
$toolIndex = Get-Content -LiteralPath (Join-Path $toolsRoot 'tool-index.json') -Raw | ConvertFrom-Json
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'clean-room-tools') -Recurse -File
$sourceLines = 0
foreach ($file in $sourceFiles | Where-Object { $_.Extension -eq '.cs' }) {
    $sourceLines += (Get-Content -LiteralPath $file.FullName).Count
}

$manifest = [ordered]@{
    name = 'BIM-AI-Studio Rebuilt 120 Tools'
    version = $Version
    builtAt = (Get-Date).ToUniversalTime().ToString('o')
    runtime = 'Revit 2027 / .NET 10 / win-x64'
    contractTools = $contracts.Count
    generatedTools = $toolIndex.Count
    sourceCsFiles = ($sourceFiles | Where-Object { $_.Extension -eq '.cs' }).Count
    sourceCsLines = $sourceLines
    packages = @('ACadSharp 3.4.9 (MIT)')
    excluded = @('Autodesk Revit API binaries', 'model files', 'logs', 'credentials')
}
$manifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding utf8

$forbidden = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object { $_.Name -in 'RevitAPI.dll', 'RevitAPIUI.dll' }
if ($forbidden.Count -gt 0) {
    throw 'Revit API binaries must not be packaged.'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Package: $zipPath"
Write-Host "SHA256: $hash"
