param([Parameter(Mandatory=$true)][string]$RunPath)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backend=Split-Path $root
$run=(Resolve-Path -LiteralPath $RunPath).Path
if(-not $run.StartsWith((Join-Path $root 'evidence/confirmed-lesson-actions/runs/').Replace('/','\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Use a confirmed-action evidence run under this workspace.'}
function Check([string]$label){if($LASTEXITCODE -ne 0){throw $label}}
$result=@{status='RUNNING';run=$run;images=@{};productionDeployment='NOT RUN'}
$file=Join-Path $run 'images.json'
try {
    foreach($pair in @(@('McpServer','vairas-mcp:local'),@('ChatBackend','vairas-chat:local'),@('Gateway','vairas-gateway:local'),@('Authentication','vairas-authentication-confirmed-actions'),@('Scheduling','vairas-scheduling-confirmed-actions'),@('VehicleManagement','vairas-vehiclemanagement-confirmed-actions'))) {
        $service=$pair[0];$tag=$pair[1]
        docker build --quiet -t $tag (Join-Path $backend "Vairas.$service");Check "Image build failed: $service"
        $result.images[$service]=@{tag=$tag;imageId=(docker image inspect --format '{{.Id}}' $tag).Trim();build='PASSED'}
    }
    & (Join-Path $backend 'Vairas.McpServer/scripts/verify-image.ps1');Check 'MCP image smoke failed.'
    & (Join-Path $backend 'Vairas.ChatBackend/scripts/verify-image.ps1');Check 'Chat image smoke failed.'
    & (Join-Path $backend 'Vairas.Gateway/scripts/verify-image.ps1');Check 'Gateway image smoke failed.'
    foreach($service in @('McpServer','ChatBackend','Gateway')) {
        $smoke=Join-Path $backend "Vairas.$service/evidence/image-smoke.json"
        Copy-Item -LiteralPath $smoke -Destination (Join-Path $run "$service-image-smoke.json")
        $result.images[$service].readiness='PASSED'
    }
    $result.status='PASSED';$result.checkedAt=(Get-Date).ToUniversalTime().ToString('o')
    $result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $file
    $summary=Join-Path $root 'evidence/confirmed-lesson-actions/verification-summary.json'
    $current=Get-Content -Raw -LiteralPath $summary | ConvertFrom-Json
    if($current.status -eq 'PASSED' -and $current.run -eq $run){$current.images='PASSED: MCP/Chat/Gateway readiness and Authentication/Scheduling/Vehicle builds';$current | Add-Member -NotePropertyName imageEvidence -NotePropertyValue $file -Force;$current | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $summary}
} catch {$result.status='FAILED';$result.error=$_.Exception.Message;$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $file;throw}
