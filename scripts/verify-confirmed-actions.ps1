param([switch]$BuildImages,[string]$ResumeRun='')
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backend=Split-Path $root
$run=if($ResumeRun){(Resolve-Path -LiteralPath $ResumeRun).Path}else{Join-Path $root ('evidence/confirmed-lesson-actions/runs/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
if(-not $run.StartsWith((Join-Path $root 'evidence/confirmed-lesson-actions/runs/').Replace('/','\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence run must be inside this workspace.'}
New-Item -ItemType Directory -Force $run | Out-Null
$summary=Join-Path $root 'evidence/confirmed-lesson-actions/verification-summary.json'
$result=@{status='RUNNING';run=$run;executionProfile='ConfirmedLessonActionsV1';defaultBusinessDispatch='DISABLED';productionDeployment='NOT RUN';liveCloud='NOT RUN';checkedAt=(Get-Date).ToUniversalTime().ToString('o');testCounts=@{}}
$previousChat=$env:VAIRAS_CHAT_EXPORT_CONTRACTS;$previousMcp=$env:VAIRAS_MCP_EXPORT_CONTRACTS
function Check([string]$label){if($LASTEXITCODE -ne 0){throw $label}}
function Report { $result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $summary }
Report
Push-Location $backend
try {
    $env:VAIRAS_CHAT_EXPORT_CONTRACTS='on';$env:VAIRAS_MCP_EXPORT_CONTRACTS='on'
    py -3 Vairas.McpServer/scripts/sync-policy.py;Check 'Shared contracts/policies differ.'
    foreach($service in @('Authentication','UserManagement','SchoolManagement','Scheduling','LearningTracking','VehicleManagement','Gateway','ChatBackend','McpServer')) {
        # Explicit resume is for an interrupted verification with unchanged already-passed service code.
        $prior=Join-Path $run "$service/regression.trx"
        if($ResumeRun -and (Test-Path -LiteralPath $prior)) {
            [xml]$passed=Get-Content -Raw -LiteralPath $prior
            if($passed.TestRun.ResultSummary.Counters -and [int]$passed.TestRun.ResultSummary.Counters.total -gt 0 -and [int]$passed.TestRun.ResultSummary.Counters.failed -eq 0){Write-Output "Reusing unchanged passed suite: $service";continue}
        }
        Push-Location (Join-Path $backend "Vairas.$service")
        try {
            $project="Vairas.$service.Tests/Vairas.$service.Tests.csproj"
            dotnet restore $project --nologo -v quiet;Check "Restore failed: $service"
            $arguments=@('test',$project,'-c','Release','--no-restore','--nologo','-v','quiet','--logger','trx;LogFileName=regression.trx','--results-directory',(Join-Path $run $service))
            if($service -in @('Authentication','LearningTracking')){$arguments+=@('--filter','TestCategory=Unit')}
            dotnet @arguments;Check "Tests failed: $service"
        } finally {Pop-Location}
    }
    Push-Location $root
    try {
        $priorServices=Join-Path $run 'services/services.trx';$servicesPassed=$false
        if($ResumeRun -and (Test-Path -LiteralPath $priorServices)){[xml]$priorReport=Get-Content -Raw -LiteralPath $priorServices;$servicesPassed=[int]$priorReport.TestRun.ResultSummary.Counters.passed -eq 2 -and [int]$priorReport.TestRun.ResultSummary.Counters.failed -eq 0}
        if(-not $servicesPassed) {
            dotnet restore Vairas.ChatBackend.ServiceTests/Vairas.ChatBackend.ServiceTests.csproj --nologo -v quiet;Check 'Service graph restore failed.'
            dotnet test Vairas.ChatBackend.ServiceTests/Vairas.ChatBackend.ServiceTests.csproj -c Release --no-restore --nologo -v quiet --logger 'trx;LogFileName=services.trx' --results-directory (Join-Path $run 'services');Check 'Real service scenarios failed.'
        } else {Write-Output 'Reusing unchanged passed real service scenarios.'}
        dotnet ef migrations has-pending-model-changes --project Vairas.ChatBackend.Persistance --startup-project Vairas.ChatBackend --configuration Release --no-build;Check 'Chat model drift.'
    } finally {Pop-Location}
    py -3 Vairas.McpServer/scripts/import-contracts.py --write;Check 'Owner output contracts differ.'
    node Vairas.Gateway/scripts/check-contracts.cjs --write;Check 'Gateway catalogue refresh failed.'
    node Vairas.Gateway/scripts/check-contracts.cjs;Check 'Gateway contract validation failed.'
    py -3 Vairas.ChatBackend/scripts/check-artifacts.py;Check 'Chat artifact validation failed.'
    py -3 Vairas.McpServer/scripts/check-artifacts.py;Check 'MCP artifact validation failed.'
    foreach($service in @('ChatBackend','McpServer')) {
        Push-Location (Join-Path $backend "Vairas.$service")
        try {
            dotnet test "Vairas.$service.Tests/Vairas.$service.Tests.csproj" -c Release --no-build --no-restore --filter Category=Unit --settings coverage.runsettings --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=unit.trx' --results-directory (Join-Path $run "$service-unit-coverage");Check "Unit coverage failed: $service"
        } finally {Pop-Location}
    }
    $result.unitLineCoverage=@{}
    foreach($service in @('ChatBackend','McpServer')) {
        $coverageFile=Get-ChildItem -LiteralPath (Join-Path $run "$service-unit-coverage") -Recurse -Filter 'coverage.cobertura.xml' | Select-Object -First 1
        if(-not $coverageFile){throw "Missing unit coverage: $service"}
        [xml]$coverage=Get-Content -Raw -LiteralPath $coverageFile.FullName
        $packages=if($service -eq 'ChatBackend'){@('Vairas.ChatBackend.Domain','Vairas.ChatBackend.Business')}else{@('Vairas.McpServer.Business','Vairas.McpServer')}
        foreach($name in $packages){$package=$coverage.coverage.packages.package | Where-Object {$_.name -eq $name};if(-not $package){throw "Missing coverage package: $name"};$rate=[Math]::Round(100*[double]$package.'line-rate',2);if($rate -lt 70){throw "Unit coverage below 70%: ${name}: $rate"};$result.unitLineCoverage[$name]=$rate}
    }
    if($BuildImages) {
        & (Join-Path $PSScriptRoot 'verify-confirmed-images.ps1') -RunPath $run;Check 'Confirmed action image verification failed.'
        $result.images='PASSED: MCP/Chat/Gateway readiness and Authentication/Scheduling/Vehicle builds'
    } else {$result.images='NOT RUN'}
    foreach($file in Get-ChildItem -LiteralPath $run -Recurse -Filter '*.trx') {
        [xml]$report=Get-Content -Raw -LiteralPath $file.FullName
        $counts=$report.TestRun.ResultSummary.Counters
        if([int]$counts.failed -ne 0){throw "Failed results: $($file.FullName)"}
        $name=$file.Directory.Name
        if($name -notlike '*unit-coverage'){$result.testCounts[$name]=@{total=[int]$counts.total;passed=[int]$counts.passed;failed=[int]$counts.failed}}
    }
    $result.status='PASSED';Report
} catch {$result.status='FAILED';$result.error=$_.Exception.Message;Report;throw}
finally {$env:VAIRAS_CHAT_EXPORT_CONTRACTS=$previousChat;$env:VAIRAS_MCP_EXPORT_CONTRACTS=$previousMcp;Pop-Location}
