[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("ping", "tools", "schema", "invoke", "agent-status", "chat")]
    [string]$Action,

    [Parameter(Position = 1)]
    [string]$Tool,

    [Parameter(Position = 2)]
    [string]$ArgumentsJson = "{}",

    [int]$TimeoutSeconds = 180,

    [string]$PipeName,

    [string]$DiscoveryPath
)

$ErrorActionPreference = "Stop"

function Get-BridgeDiscoveryCandidates {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($DiscoveryPath)) {
        $candidates += $DiscoveryPath
    }

    $candidates += Join-Path $env:LOCALAPPDATA "RevitAi\codex-bridge.json"
    $candidates += Join-Path $env:LOCALAPPDATA "RevitAi\bridge.json"
    $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
}

function Read-BridgeDiscovery {
    $stale = @()
    foreach ($candidate in Get-BridgeDiscoveryCandidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            $discovery = Get-Content -Raw -LiteralPath $candidate | ConvertFrom-Json
            if ([string]::IsNullOrWhiteSpace($discovery.pipeName)) {
                throw "Discovery file does not contain a pipeName."
            }

            if ($null -ne $discovery.processId) {
                $process = Get-Process -Id ([int]$discovery.processId) -ErrorAction SilentlyContinue
                if ($null -eq $process) {
                    $stale += "Process $($discovery.processId) is not running."
                    continue
                }
            }

            return $discovery
        }
        catch {
            $stale += "$candidate : $($_.Exception.Message)"
        }
    }

    $detail = if ($stale.Count -gt 0) { " Stale or invalid files: $($stale -join '; ')" } else { "" }
    throw "No live Revit bridge discovery file was found. Codex bridge: $env:LOCALAPPDATA\RevitAi\codex-bridge.json; Revit AI bridge: $env:LOCALAPPDATA\RevitAi\bridge.json.$detail"
}

function Invoke-BridgeRequest {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Request
    )

    $resolvedPipeName = $PipeName
    $discovery = $null
    if ([string]::IsNullOrWhiteSpace($resolvedPipeName)) {
        $discovery = Read-BridgeDiscovery
        $resolvedPipeName = [string]$discovery.pipeName
    }

    if ($Request.type -eq "invoke" -and -not $Request.ContainsKey("expectedDocumentGuid")) {
        if ($null -eq $discovery) {
            $discovery = Read-BridgeDiscovery
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$discovery.documentGuid)) {
            $Request.expectedDocumentGuid = [string]$discovery.documentGuid
        }
    }

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $resolvedPipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)

    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $reader = $null
    $writer = $null

    try {
        $pipe.Connect($TimeoutSeconds * 1000)
        $reader = [System.IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
        $writer.AutoFlush = $true

        $json = $Request | ConvertTo-Json -Depth 100 -Compress
        $writer.WriteLine($json)

        $readTask = $reader.ReadLineAsync()
        if (-not $readTask.Wait($TimeoutSeconds * 1000)) {
            throw "Timed out waiting for the Revit bridge response."
        }

        $responseText = $readTask.Result
        if ([string]::IsNullOrWhiteSpace($responseText)) {
            throw "The Revit bridge closed the connection without a response."
        }

        return ($responseText | ConvertFrom-Json)
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        $pipe.Dispose()
    }
}

function Write-Json {
    param(
        [Parameter(Mandatory = $true)]
        $Value
    )

    $Value | ConvertTo-Json -Depth 100
}

switch ($Action) {
    "agent-status" {
        Write-Json (Invoke-BridgeRequest -Request @{ type = "agent.status" })
        break
    }

    "chat" {
        if ([string]::IsNullOrWhiteSpace($Tool)) {
            throw "The chat action requires a natural-language message."
        }

        Write-Json (Invoke-BridgeRequest -Request @{ type = "agent.chat"; message = $Tool })
        break
    }

    "ping" {
        Write-Json (Invoke-BridgeRequest -Request @{ type = "ping" })
    }

    "tools" {
        Write-Json (Invoke-BridgeRequest -Request @{ type = "tools.list" })
    }

    "schema" {
        if ([string]::IsNullOrWhiteSpace($Tool)) {
            throw "The schema action requires a tool name."
        }

        $catalog = Invoke-BridgeRequest -Request @{ type = "tools.list" }
        $definition = $catalog.tools |
            Where-Object { $_.name -ieq $Tool } |
            Select-Object -First 1

        if ($null -eq $definition) {
            throw "Tool '$Tool' was not found in the live Revit catalog."
        }

        Write-Json $definition
    }

    "invoke" {
        if ([string]::IsNullOrWhiteSpace($Tool)) {
            throw "The invoke action requires a tool name."
        }

        $arguments = $ArgumentsJson | ConvertFrom-Json
        $request = @{
            type = "invoke"
            requestId = [Guid]::NewGuid().ToString("N")
            tool = $Tool
            arguments = $arguments
        }

        $response = Invoke-BridgeRequest -Request $request
        Write-Json $response

        if ($response.success -ne $true) {
            exit 2
        }
    }
}