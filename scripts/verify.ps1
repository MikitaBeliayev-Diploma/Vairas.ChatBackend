param([switch]$IncludeServices, [switch]$BuildImage)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function CheckExit([string]$message) { if ($LASTEXITCODE -ne 0) { throw $message } }
Push-Location $root
$previousExport = $env:VAIRAS_CHAT_EXPORT_CONTRACTS
try {
    $run = Join-Path $root ('evidence/runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $run | Out-Null
    @{ checkedAt=(Get-Date).ToUniversalTime().ToString('o'); run=$run; status='RUNNING' } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/verification-summary.json')
    if (-not (Test-Path (Join-Path $root 'evidence/image-smoke.json'))) { @{ status='NOT RUN' } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/image-smoke.json') }
    py -3 scripts/check-artifacts.py; CheckExit 'Source or documentation validation failed.'
    dotnet restore Vairas.ChatBackend.slnx; CheckExit 'Restore or NuGet audit failed.'
    dotnet tool restore; CheckExit 'EF tool restore failed.'
    dotnet build Vairas.ChatBackend.slnx -c Release --no-restore; CheckExit 'Release build failed.'
    dotnet ef migrations has-pending-model-changes --project Vairas.ChatBackend.Persistance --startup-project Vairas.ChatBackend --configuration Release --no-build
    CheckExit 'EF model and migration drift.'
    dotnet test Vairas.ChatBackend.Tests -c Release --no-build --no-restore --filter Category=Unit --settings coverage.runsettings --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=unit.trx' --results-directory (Join-Path $run 'unit')
    CheckExit 'Unit tests failed.'
    $file = Get-ChildItem (Join-Path $run 'unit') -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
    if (-not $file) { throw 'Fresh unit coverage report missing.' }
    [xml]$coverage = Get-Content -Raw $file.FullName
    $rates = @{}
    foreach ($name in @('Vairas.ChatBackend.Domain','Vairas.ChatBackend.Business')) {
        $package = $coverage.coverage.packages.package | Where-Object { $_.name -eq $name }
        if (-not $package) { throw "Coverage package missing: $name" }
        $percent = [Math]::Round(100 * [double]$package.'line-rate', 2)
        if ($percent -lt 70) { throw "Unit line coverage below 70% for ${name}: $percent" }; $rates[$name]=$percent
    }
    $env:VAIRAS_CHAT_EXPORT_CONTRACTS = 'on'
    dotnet test Vairas.ChatBackend.Tests -c Release --no-build --no-restore --filter 'Category=Http|Category=Adapter' --logger 'trx;LogFileName=http-adapter.trx' --results-directory (Join-Path $run 'http-adapter')
    CheckExit 'HTTP or SDK adapter tests failed.'
    dotnet test Vairas.ChatBackend.Tests -c Release --no-build --no-restore --filter Category=Postgres --logger 'trx;LogFileName=postgres.trx' --results-directory (Join-Path $run 'postgres')
    CheckExit 'Native PostgreSQL tests failed.'
    $services='NOT RUN'; $image='NOT RUN'
    if ($IncludeServices) {
        dotnet restore Vairas.ChatBackend.ServiceTests; CheckExit 'Owner service smoke restore failed.'
        dotnet test Vairas.ChatBackend.ServiceTests -c Release --no-restore --logger 'trx;LogFileName=services.trx' --results-directory (Join-Path $run 'services')
        CheckExit 'Real owner service smoke failed.'; $services='PASSED'
    }
    node ../Vairas.Gateway/scripts/check-contracts.cjs; CheckExit 'Gateway catalogue drift; review exports before --write.'
    dotnet restore ../Vairas.Gateway/Vairas.Gateway.slnx; CheckExit 'Gateway restore failed.'
    dotnet test ../Vairas.Gateway/Vairas.Gateway.slnx -c Release --no-restore --logger 'trx;LogFileName=gateway.trx' --results-directory (Join-Path $run 'gateway')
    CheckExit 'Gateway regression failed.'
    if ($BuildImage) {
        docker build -t vairas-chat:local .; CheckExit 'Chat Docker build failed.'
        & (Join-Path $PSScriptRoot 'verify-image.ps1'); $image='PASSED'
    }
    $audit = dotnet list Vairas.ChatBackend.slnx package --vulnerable --include-transitive --format json
    CheckExit 'Dependency vulnerability query failed.'
    $audit | Set-Content -Encoding UTF8 (Join-Path $run 'dependency-audit.json')
    $data = ($audit -join "`n") | ConvertFrom-Json
    foreach ($project in $data.projects) { foreach ($framework in $project.frameworks) { foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) { if ($package.vulnerabilities) { throw 'Vulnerable dependency detected.' } } } }
    py -3 scripts/check-artifacts.py; CheckExit 'Final artifacts invalid.'
    @{ checkedAt=(Get-Date).ToUniversalTime().ToString('o'); run=$run; unitLineCoverage=$rates; postgres='PASSED'; services=$services; image=$image; liveCloud='NOT RUN'; businessDispatch='DISABLED'; productionDeployment='NOT RUN' } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/verification-summary.json')
    py -3 scripts/summarize.py; CheckExit 'Cannot summarize fresh test results.'
    Get-Content (Join-Path $root 'evidence/verification-summary.json')
} catch {
    @{ checkedAt=(Get-Date).ToUniversalTime().ToString('o'); run=$run; status='FAILED'; note='See console and fresh run artifacts; a failed verification is not a passing result.' } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/verification-summary.json')
    throw
} finally { $env:VAIRAS_CHAT_EXPORT_CONTRACTS=$previousExport; Pop-Location }
