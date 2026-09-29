[CmdletBinding()]
param(
    [ValidateSet('All', 'Correctness', 'Timing', 'Performance')]
    [string]$Suite = 'All',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ArtifactsPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$testProject = Join-Path $repoRoot 'apps/pos-wpf/tests/Hbpos.Client.Tests/Hbpos.Client.Tests.csproj'
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $repoRoot '.artifacts/wpf-card-regression'
}
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
# 每次保留独立结果，防止旧 TRX 被误当作本轮通过证据；构建产物可复用。
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$resultsPath = Join-Path $ArtifactsPath "test-results/$runId"
New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null

# 按 FullyQualifiedName 包含匹配；同一族的测试类用前缀（如 CardRecovery）一次纳入，新增同族测试自动进入回归。
$testClasses = @(
    'CashPaymentWorkflowService'
    'CardPaymentRecoveryServiceTests'
    'SquarePaymentRecoveryService'
    'ConfiguredCardTerminalClientTests'
    'ConfiguredLinklyTerminalClientTests'
    'LinklyTerminalClientTests'
    'LinklyCloudTerminalClientTests'
    'LinklyCloudApiClientTests'
    'LinklyBackendTerminalClient'
    'LinklyRecoveryServiceGateTests'
    'LinklyTerminalSelectionTransitionGateTests'
    'SquareTerminalPaymentClientTests'
    'RecoveryCasRepository'
    'CardRecovery'
    'CardRefund'
    'CardPaymentHandoff'
    'ManualCardPayment'
    'PaymentTerminalSelectionViewModelTests'
    'PaymentViewLayoutTests'
    'PaymentViewRuntimeTests'
    'PaymentPageRedesignRuntimeTests'
    'WpfViewLifecycleTests'
    'PaymentFlowIntegrationTests'
    'MainViewModelScannerTests'
    'PosTerminalCashPaymentViewModelTests'
    'LocalCardPaymentAttemptRepositoryTests'
)
$scopeFilter = '(' + (($testClasses | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')'
$filters = [ordered]@{
    Correctness = "$scopeFilter&Category!=Performance&Category!=Timing"
    Timing = "$scopeFilter&Category=Timing&Category!=Performance"
    Performance = "$scopeFilter&Category=Performance"
}
$selectedSuites = if ($Suite -eq 'All') { @($filters.Keys) } else { @($Suite) }

# 先完成 restore/build，再顺序执行分组，避免构建负载干扰性能阈值。
& dotnet build $testProject --configuration $Configuration --artifacts-path $ArtifactsPath --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    throw "刷卡回归测试工程构建失败，退出码：$LASTEXITCODE"
}

# 测试类改名或删除后，清单条目会静默匹配 0 个测试；先确认每一项至少命中一个测试（与过滤 ~ 一样不区分大小写）。
$listing = @(& dotnet test $testProject --configuration $Configuration --artifacts-path $ArtifactsPath `
    --no-build --no-restore --list-tests)
if ($LASTEXITCODE -ne 0) {
    throw "刷卡回归测试清单读取失败，退出码：$LASTEXITCODE"
}
$testNames = @($listing | Where-Object { $_ -match '^\s{4}\S' } | ForEach-Object { $_.Trim() })
if ($testNames.Count -eq 0) {
    throw '刷卡回归测试清单为空，无法校验类清单。'
}
$staleEntries = @()
foreach ($entry in $testClasses) {
    $hit = $false
    foreach ($testName in $testNames) {
        if ($testName.IndexOf($entry, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $hit = $true
            break
        }
    }
    if (-not $hit) {
        $staleEntries += $entry
    }
}
if ($staleEntries.Count -gt 0) {
    throw "刷卡回归类清单中以下条目未匹配任何测试，请同步改名或删除：$($staleEntries -join ', ')"
}

$summary = @()
foreach ($group in $selectedSuites) {
    Write-Host "执行刷卡回归分组：$group"
    $trxName = "$group.trx"
    & dotnet test $testProject --configuration $Configuration --artifacts-path $ArtifactsPath `
        --no-build --no-restore --filter $filters[$group] --results-directory $resultsPath `
        --logger "trx;LogFileName=$trxName" --verbosity quiet
    $testExitCode = $LASTEXITCODE
    $trxPath = Join-Path $resultsPath $trxName
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        $summary += [pscustomobject]@{ Suite = $group; Total = 0; Passed = 0; Failed = 0; ExitCode = $testExitCode; Valid = $false }
        continue
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $total = [int]$counters.total
    $executed = [int]$counters.executed
    $passed = [int]$counters.passed
    # 零测试、跳过或缺失结果均不能被报告为该分组通过。
    $valid = $testExitCode -eq 0 -and $total -gt 0 -and $executed -eq $total -and
        $passed -eq $total -and $trx.TestRun.ResultSummary.outcome -eq 'Completed'
    foreach ($counterName in @('failed', 'error', 'timeout', 'aborted', 'inconclusive',
        'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning',
        'inProgress', 'pending')) {
        if ($counters.HasAttribute($counterName) -and [int]$counters.GetAttribute($counterName) -ne 0) {
            $valid = $false
        }
    }
    $summary += [pscustomobject]@{
        Suite = $group
        Total = $total
        Passed = $passed
        Failed = [int]$counters.failed
        ExitCode = $testExitCode
        Valid = $valid
    }
}

$summary | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $resultsPath 'summary.json') -Encoding utf8
$summary | Format-Table -AutoSize | Out-Host
Write-Host "测试结果：$resultsPath"
if (@($summary | Where-Object { -not $_.Valid }).Count -gt 0) {
    throw '刷卡回归存在失败、跳过或未执行的分组，请检查对应 TRX。'
}
