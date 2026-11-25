param([string]$Image = 'vairas-chat:local')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$suffix = [Guid]::NewGuid().ToString('N')
$network = 'chat-check-' + $suffix
$pg = 'chat-pg-' + $suffix
$api = 'chat-api-' + $suffix
$password = [Guid]::NewGuid().ToString('N')
$createdNetwork = $false; $createdPg = $false; $createdApi = $false
function CheckExit([string]$message) { if ($LASTEXITCODE -ne 0) { throw $message } }
try {
    docker network create $network | Out-Null; CheckExit 'Cannot create isolated Docker network.'; $createdNetwork = $true
    docker run --rm -d --name $pg --network $network --network-alias database -e "POSTGRES_PASSWORD=$password" postgres:17 | Out-Null
    CheckExit 'Cannot start isolated PostgreSQL.'; $createdPg = $true
    $ready = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        docker exec $pg pg_isready -h 127.0.0.1 -U postgres 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }; Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'PostgreSQL did not start.' }
    @"
CREATE ROLE vairas_chat_migration LOGIN PASSWORD '$password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE vairas_chat_runtime LOGIN PASSWORD '$password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE vairas_chat_operator LOGIN PASSWORD '$password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE DATABASE vairas_chat OWNER vairas_chat_migration;
"@ | docker exec -i $pg psql -X -v ON_ERROR_STOP=1 -U postgres | Out-Null
    CheckExit 'Cannot provision isolated roles.'
    Get-Content -Raw (Join-Path $root 'database/prepare-schema.sql') | docker exec -i $pg psql -X -v ON_ERROR_STOP=1 -U postgres -d vairas_chat | Out-Null
    CheckExit 'Cannot prepare isolated schema.'
    $migration = "Host=database;Database=vairas_chat;Username=vairas_chat_migration;Password=$password"
    $deployment = '00000000-0000-4000-8000-000000000001'
    for ($pass=0; $pass -lt 2; $pass++) {
        docker run --rm --network $network -e "VAIRAS_CHAT_MIGRATION_CONNECTION=$migration" -e "Store__DeploymentId=$deployment" -e 'Model__Name=synthetic' -e 'Model__Deployment=synthetic' -e 'Model__Region=local' $Image --initialize --register-model
        CheckExit 'Image initialization or idempotent deployment registration failed.'
    }
    $runtime = "Host=database;Database=vairas_chat;Username=vairas_chat_runtime;Password=$password"
    docker run --rm -d --name $api --network $network -p '127.0.0.1::8080' -e "ConnectionStrings__Chat=$runtime" -e "Dependencies__ClientSecret=$password" $Image | Out-Null
    CheckExit 'Cannot start Chat image.'; $createdApi = $true
    $endpoint = (docker port $api 8080/tcp).Trim(); CheckExit 'No container port.'
    if ($endpoint -notmatch '^127\.0\.0\.1:\d+$') { throw 'Only loopback exposure is allowed for smoke.' }
    $base = 'http://' + $endpoint; $ready = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        try { if ((Invoke-WebRequest -UseBasicParsing -Uri "$base/health/ready" -Headers @{ Host='localhost' } -TimeoutSec 2).StatusCode -eq 200) { $ready=$true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Restricted runtime readiness failed.' }
    $user = (docker inspect --format '{{.Config.User}}' $api).Trim()
    if ($user -in @('', 'root', '0')) { throw 'Image must run as nonroot.' }
    try { Invoke-WebRequest -UseBasicParsing -Uri "$base/api/v1/chat/sessions?schoolId=$deployment" -Headers @{ Host='localhost'; 'X-Forwarded-Proto'='https' } -TimeoutSec 3 | Out-Null; throw 'Cleartext ingress unexpectedly accepted.' }
    catch { if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 403) { throw } }
    $enabled = (docker exec $pg psql -X -U postgres -d vairas_chat -tAc 'SELECT enabled FROM ai_model_deployment').Trim(); CheckExit 'Cannot verify disabled deployment.'
    if ($enabled -ne 'f') { throw 'New model must remain disabled.' }
    $evidence = @{ checkedAt=(Get-Date).ToUniversalTime().ToString('o'); image=$Image; imageId=(docker inspect --format '{{.Image}}' $api).Trim(); nonRootUser=$user; readiness=200; cleartextApi=403; migrationsAndRegistration='PASSED TWICE'; nativePostgreSql=17; modelEnabled=$false; cloud='NOT RUN'; ownerConnectivity='NOT RUN' }
    $evidence | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/image-smoke.json')
    $evidence | ConvertTo-Json
} finally {
    if ($createdApi) { docker rm -f $api | Out-Null }
    if ($createdPg) { docker rm -f $pg | Out-Null }
    if ($createdNetwork) { docker network rm $network | Out-Null }
}
