param(
    [Parameter(Mandatory = $true)]
    [string] $ComposeFile,

    [Parameter(Mandatory = $true)]
    [string] $DumpFile
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $DumpFile)) {
    Write-Error "Dump file not found: $DumpFile"
    exit 1
}

$DbUser = if ($env:ROUTETIMER_DB_USER) { $env:ROUTETIMER_DB_USER } else { "routetimer" }
$DbName = if ($env:ROUTETIMER_DB_NAME) { $env:ROUTETIMER_DB_NAME } else { "routetimer" }

# See the matching comment in backup.ps1: docker compose interpolates every service's environment
# before running any command, so the local model's required GARMIN_TOKEN_ENCRYPTION_KEY must
# resolve even for this routetimer-db-only exec.
$ComposeArgs = @("-f", $ComposeFile)
$EnvFile = Join-Path (Split-Path $ComposeFile -Parent) ".env.local"
if (Test-Path $EnvFile) {
    $ComposeArgs += @("--env-file", $EnvFile)
}

Get-Content -Path $DumpFile -AsByteStream -Raw |
    docker compose @ComposeArgs exec -T routetimer-db pg_restore --clean --if-exists -U $DbUser -d $DbName
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_restore failed."
    exit 1
}

Write-Host "Restored from $DumpFile"
