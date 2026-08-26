param(
    [Parameter(Mandatory = $true)]
    [string] $ComposeFile,

    [string] $OutDir = "."
)

$ErrorActionPreference = "Stop"

$DbUser = if ($env:ROUTETIMER_DB_USER) { $env:ROUTETIMER_DB_USER } else { "routetimer" }
$DbName = if ($env:ROUTETIMER_DB_NAME) { $env:ROUTETIMER_DB_NAME } else { "routetimer" }
$Timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutFile = Join-Path $OutDir "routetimer-$Timestamp.dump"

# docker compose interpolates every service's environment before running any command, even an
# exec against a single service -- so the local model's required GARMIN_TOKEN_ENCRYPTION_KEY must
# resolve here too, even though this script never touches the routetimer service itself. The
# homelab model has no such file next to its compose file, so this is a no-op there.
$ComposeArgs = @("-f", $ComposeFile)
$EnvFile = Join-Path (Split-Path $ComposeFile -Parent) ".env.local"
if (Test-Path $EnvFile) {
    $ComposeArgs += @("--env-file", $EnvFile)
}

docker compose @ComposeArgs exec -T routetimer-db pg_dump -Fc -U $DbUser $DbName |
    Set-Content -Path $OutFile -AsByteStream
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_dump failed."
    exit 1
}

Write-Host "Backup written to $OutFile"
