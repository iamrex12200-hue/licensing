<#
.SYNOPSIS
    Deploy and maintain the self-hosted licensing stack (Flask + Caddy + SQLite via Docker Compose).

.DESCRIPTION
    Commands:
      check    - verify Docker/Compose, .env presence, and required secrets
      up       - build & start the stack, then poll /healthz until healthy
      backup   - export the lic-data volume as a timestamped .tgz (with retention)
      logs     - follow live container logs
      down     - stop the stack (volumes are kept)

    Examples:
      .\deploy-licensing.ps1 check
      .\deploy-licensing.ps1 up
      .\deploy-licensing.ps1 backup -Keep 14
      .\deploy-licensing.ps1 logs
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('check', 'up', 'backup', 'logs', 'down')]
    [string]$Command,

    [Parameter()]
    [ValidateRange(1, 90)]
    [int]$Keep = 14,

    [Parameter()]
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$script:Root = $PSScriptRoot

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-ErrorExit([string]$Message, [int]$Code = 1) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit $Code
}

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Get-ComposePrefix {
    # Returns an array: @('compose') for `docker compose`, empty for legacy docker-compose.
    # Note: `return ,@(...)` preserves the array - a bare `return @(...)` would be
    # unrolled by PowerShell into a scalar string, which then splats as characters.
    if (Test-Command 'docker') {
        docker compose version 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return ,@('compose') }
    }
    if (Test-Command 'docker-compose') {
        docker-compose --version 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return ,@() }
    }
    return $null
}

function Get-EnvFile {
    $envPath = Join-Path $script:Root '.env'
    if (-not (Test-Path -LiteralPath $envPath)) {
        Write-ErrorExit "'.env' not found in $script:Root (create it from compose.yml requirements)."
    }
    return $envPath
}

function Read-EnvVars([string]$EnvPath) {
    $vars = @{}
    foreach ($line in (Get-Content -LiteralPath $EnvPath -ErrorAction Stop)) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -match '^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            $name = $matches[1]
            $value = $matches[2].Trim()
            if ($value.Length -ge 2 -and
                (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                 ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $vars[$name] = $value
        }
    }
    return $vars
}

function Invoke-Check {
    Write-Step 'Checking prerequisites...'

    if (-not (Test-Command 'docker')) {
        Write-ErrorExit 'Docker is not installed or not on PATH.'
    }
    docker --version
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorExit 'Docker CLI failed to run (is the daemon/docker engine installed?).'
    }

    $compose = Get-ComposePrefix
    if ($null -eq $compose) {
        Write-ErrorExit 'Docker Compose is not available (neither "docker compose" nor "docker-compose").'
    }
    Write-Host 'Docker Compose: OK'

    $envPath = Get-EnvFile
    $vars = Read-EnvVars $envPath
    Write-Host "Loaded .env with $($vars.Count) variable(s): $((@($vars.Keys) -join ', '))"

    $missing = @()
    foreach ($name in @('LIC_ADMIN_KEY', 'LIC_HMAC_SECRET')) {
        if (-not $vars.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($vars[$name])) {
            $missing += $name
        }
    }
    if ($missing.Count -gt 0) {
        Write-ErrorExit "Required environment variable(s) not set in .env: $($missing -join ', ')"
    }

    foreach ($name in @('LIC_ADMIN_KEY', 'LIC_HMAC_SECRET')) {
        $value = $vars[$name]
        if ($value -match 'change-me' -or $value.Length -lt 16) {
            Write-Host "WARNING: $name looks weak (shorter than 16 chars or placeholder) - " `
                'use a long random value.' -ForegroundColor Yellow
        }
    }

    Write-Host 'All prerequisites OK.' -ForegroundColor Green
}

function Invoke-Compose {
    # Applies the detected compose prefix and splats args onto docker/docker-compose.
    param([Parameter(Mandatory)][object[]]$Args)
    $prefix = Get-ComposePrefix
    if ($null -eq $prefix) {
        Write-ErrorExit 'Docker Compose is not available.'
    }
    if (Test-Command 'docker') {
        & docker @prefix @Args
    }
    else {
        & docker-compose @Args
    }
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorExit "docker compose $($Args -join ' ') failed (exit $LASTEXITCODE)."
    }
}

function Test-Healthz {
    $prefix = Get-ComposePrefix
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $attempt = 0
    do {
        $attempt++
        $probe = "import urllib.request,json;" + "print(json.load(urllib.request.urlopen('http://127.0.0.1:8000/healthz',timeout=3)).get('status',''))"
        $out = & docker @prefix exec -T license python -c $probe 2>$null
        if ($LASTEXITCODE -eq 0 -and $out -eq 'ok') {
            Write-Host "Healthz OK (attempt $attempt)." -ForegroundColor Green
            return $true
        }
        if ($attempt -eq 1) {
            Write-Host "Waiting for /healthz (timeout ${HealthTimeoutSeconds}s)..."
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Invoke-Up {
    Write-Step 'Building and starting the stack...'
    Invoke-Compose @('up', '-d', '--build')

    if (-not (Test-Healthz)) {
        Write-Host '--- last 25 log lines (diagnostics) ---' -ForegroundColor Yellow
        & docker $(Get-ComposePrefix) logs --tail 25 license 2>&1 | Out-String | Write-Host
        Write-ErrorExit "/healthz did not report OK within ${HealthTimeoutSeconds}s."
    }
    & docker $(Get-ComposePrefix) ps
}

function Invoke-Backup {
    param([int]$KeepCount)
    $backupDir = Join-Path $script:Root 'backups'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $fileName = "lic-data-$timestamp.tgz"
    $localFile = Join-Path $backupDir $fileName
    Write-Step "Exporting volume 'lic-data' -> $localFile"

    & docker run --rm `
        -v lic-data:/data `
        "-v ${backupDir}:/backup" `
        alpine tar czf "/backup/$fileName" -C /data .
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorExit 'Backup failed (tar exited non-zero).'
    }

    $size = '{0:N1} MB' -f ((Get-Item $localFile).Length / 1MB)
    Write-Host "Backup complete: $fileName ($size)" -ForegroundColor Green

    $old = @(Get-ChildItem -Path $backupDir -Filter 'lic-data-*.tgz' `
        | Sort-Object LastWriteTime -Descending | Select-Object -Skip $KeepCount)
    if ($old.Count -gt 0) {
        Write-Step "Pruning $($old.Count) backup(s) older than the newest $KeepCount..."
        $old | Remove-Item -Force
    }
}

function Invoke-Logs {
    & docker $(Get-ComposePrefix) logs -f
}

function Invoke-Down {
    Write-Step 'Stopping the stack (volumes and data are kept)...'
    Invoke-Compose @('down')
}

# --- dispatch ---
if (-not $Command) {
    Write-Host 'Usage: .\deploy-licensing.ps1 <check|up|backup|logs|down> [options]'
    Write-Host '  backup: -Keep <n>  (retain newest n archives, default 14)'
    exit 0
}

switch ($Command) {
    'check'  { Invoke-Check }
    'up'     { Invoke-Up }
    'backup' { Invoke-Backup -KeepCount $Keep }
    'logs'   { Invoke-Logs }
    'down'   { Invoke-Down }
}