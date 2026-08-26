$ErrorActionPreference = "Stop"

$Port = if ($env:ROUTETIMER_PORT) { $env:ROUTETIMER_PORT } else { "49215" }
$ComposeFile = Join-Path $PSScriptRoot "deploy\docker-compose.local.yml"
$EnvFile = Join-Path $PSScriptRoot "deploy\.env.local"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker is not installed. See RUNBOOK.md, Step 1."
    exit 1
}

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker is installed but not running. Start Docker Desktop, then run this again."
    exit 1
}

if (-not (Test-Path $EnvFile)) {
    Write-Host "First run: generating a Garmin token encryption key..."
    # This key encrypts your Garmin account tokens at rest -- generated once, kept in this
    # git-ignored file, and reused on every future start. Losing it makes any stored Garmin
    # connection unreadable; RouteTimer's own training data and predictions are unaffected.
    $KeyBytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($KeyBytes)
    $Key = [Convert]::ToBase64String($KeyBytes)
    "GARMIN_TOKEN_ENCRYPTION_KEY=$Key" | Out-File -FilePath $EnvFile -Encoding ascii -NoNewline
}

Write-Host "Starting RouteTimer on port $Port..."
$env:ROUTETIMER_PORT = $Port
docker compose -f $ComposeFile --env-file $EnvFile up -d --pull always --wait
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Startup failed. If the error above mentions the port already being in use," -ForegroundColor Yellow
    Write-Host "run again with a different port, e.g.:" -ForegroundColor Yellow
    Write-Host '  $env:ROUTETIMER_PORT="49999"; .\run.ps1' -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If it instead timed out waiting for the app to become healthy, it may still be" -ForegroundColor Yellow
    Write-Host "applying database migrations on first run -- check:" -ForegroundColor Yellow
    Write-Host "  docker compose -f deploy\docker-compose.local.yml logs -f routetimer" -ForegroundColor Yellow
    exit 1
}

$Url = "http://localhost:$Port"
Write-Host ""
Write-Host "RouteTimer is running at $Url"
Start-Process $Url
