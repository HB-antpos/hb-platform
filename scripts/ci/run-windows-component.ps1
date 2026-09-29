param(
  [Parameter(Mandatory = $true)]
  [string]$Component,
  [ValidateSet('pr', 'weekly')]
  [string]$Profile = 'pr',
  [ValidateSet(
    'all',
    'noop',
    'client-a-b-d-h',
    'client-c-card',
    'client-c-other',
    'client-i-k-m-n',
    'client-l-linkly',
    'client-l-other',
    'client-o-r',
    'client-s-shared',
    'client-s-other',
    'client-t-z',
    'ui')]
  [string]$Shard = 'all'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repositoryRoot

if ($Component -eq 'noop' -or $Shard -eq 'noop') {
  Write-Host '该 PR 没有需要 Windows runner 执行的组件。'
  exit 0
}
if ($Component -ne 'pos-wpf') {
  throw "未知 Windows 组件：$Component"
}

$resultsRoot = if ($env:RUNNER_TEMP) {
  Join-Path $env:RUNNER_TEMP 'pos-wpf'
} else {
  Join-Path $repositoryRoot '.artifacts\ci\pos-wpf'
}
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

function Invoke-TestProject {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Project,
    [Parameter(Mandatory = $true)]
    [string]$Filter,
    [Parameter(Mandatory = $true)]
    [string]$LogFileName,
    [Parameter(Mandatory = $true)]
    [string]$Label,
    [Parameter(Mandatory = $true)]
    [string]$Destination
  )

  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  dotnet test $Project `
    --configuration Release `
    --no-build `
    --filter $Filter `
    --logger "trx;LogFileName=$LogFileName" `
    --results-directory $Destination
  Assert-TrxTests -Path (Join-Path $Destination $LogFileName) -Label $Label
}

function Assert-TrxTests {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [Parameter(Mandatory = $true)]
    [string]$Label
  )

  if (-not (Test-Path -LiteralPath $Path)) {
    throw "$Label 缺少 TRX：$Path"
  }
  [xml]$trx = Get-Content -LiteralPath $Path -Raw
  $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
  if ($null -eq $counters) {
    throw "$Label TRX 缺少 Counters"
  }
  $requiredCounterNames = @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted')
  $counterValues = @{}
  foreach ($counterName in $requiredCounterNames) {
    if (-not $counters.HasAttribute($counterName)) {
      throw "$Label TRX 缺少关键计数：$counterName"
    }

    $count = 0
    if (-not [int]::TryParse($counters.GetAttribute($counterName), [ref]$count)) {
      throw "$Label TRX 计数无效：$counterName"
    }
    $counterValues[$counterName] = $count
  }
  if ($counterValues.total -lt 1) {
    throw "$Label 执行测试数为 0，拒绝假绿"
  }
  if ($counterValues.total -ne $counterValues.executed -or $counterValues.total -ne $counterValues.passed) {
    throw "$Label TRX 计数不一致：total=$($counterValues.total), executed=$($counterValues.executed), passed=$($counterValues.passed)"
  }
  foreach ($counterName in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
    if ($counters.HasAttribute($counterName)) {
      $count = 0
      if (-not [int]::TryParse($counters.GetAttribute($counterName), [ref]$count)) {
        throw "$Label TRX 计数无效：$counterName"
      }
      if ($count -ne 0) {
        throw "$Label 存在非成功结果：$counterName=$count"
      }
    }
  }
  Write-Host "$Label TRX 验证通过：executed=$($counterValues.executed), passed=$($counterValues.passed)"
}

$testFilter = if ($Profile -eq 'weekly') {
  $env:HBPOS_RUN_PERF_TESTS = '1'
  'Category!=LiveE2e'
} else {
  'Category!=Performance&Category!=LiveE2e'
}

dotnet restore apps/pos-wpf/hbpos_win.slnx
dotnet build apps/pos-wpf/hbpos_win.slnx --configuration Release --no-restore

$clientTestsProject = 'apps/pos-wpf/tests/Hbpos.Client.Tests/Hbpos.Client.Tests.csproj'
$clientTestNamespace = 'Hbpos.Client.Tests.'

# 分片按类名前缀划分：命中任一 Include 且不命中任何 Exclude。同一份定义既生成 dotnet test
# 过滤表达式，也用于完整性校验，保证两者不会各自漂移。
$clientShards = [ordered]@{
  'client-a-b-d-h' = @{ Include = @('A', 'B', 'D', 'E', 'F', 'G', 'H'); Exclude = @() }
  'client-c-card' = @{ Include = @('Card'); Exclude = @() }
  'client-c-other' = @{ Include = @('C'); Exclude = @('Card') }
  'client-i-k-m-n' = @{ Include = @('I', 'J', 'K', 'M', 'N'); Exclude = @() }
  'client-l-linkly' = @{ Include = @('Linkly'); Exclude = @() }
  'client-l-other' = @{ Include = @('L'); Exclude = @('Linkly') }
  'client-o-r' = @{ Include = @('O', 'P', 'Q', 'R'); Exclude = @() }
  'client-s-shared' = @{ Include = @('Shared'); Exclude = @() }
  'client-s-other' = @{ Include = @('S'); Exclude = @('Shared') }
  'client-t-z' = @{ Include = @('T', 'U', 'V', 'W', 'X', 'Y', 'Z'); Exclude = @() }
}

function Get-ClientShardFilter {
  param([Parameter(Mandatory = $true)][hashtable]$Definition)

  $include = ($Definition.Include | ForEach-Object { "FullyQualifiedName~$clientTestNamespace$_" }) -join '|'
  $filter = "($include)"
  foreach ($prefix in $Definition.Exclude) {
    $filter += "&(FullyQualifiedName!~$clientTestNamespace$prefix)"
  }
  return $filter
}

function Test-ClientShardMatch {
  param(
    [Parameter(Mandatory = $true)][hashtable]$Definition,
    [Parameter(Mandatory = $true)][string]$TestName
  )

  # 与 VSTest 过滤的 ~ 语义一致：不区分大小写的包含匹配。用循环而不是管道，几千个测试逐片判断也很快。
  foreach ($prefix in $Definition.Exclude) {
    if ($TestName.IndexOf("$clientTestNamespace$prefix", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
      return $false
    }
  }
  foreach ($prefix in $Definition.Include) {
    if ($TestName.IndexOf("$clientTestNamespace$prefix", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
      return $true
    }
  }
  return $false
}

function Assert-ClientShardCoverage {
  # 分片过滤只校验"每片 >0 个测试"，漏片的测试会被静默跳过；这里用全量清单确认每个测试恰好属于一个分片。
  $env:DOTNET_CLI_UI_LANGUAGE = 'en'
  $env:VSLANG = '1033'
  $listing = @(dotnet test $clientTestsProject --configuration Release --no-build --list-tests)
  $headerIndex = -1
  for ($i = 0; $i -lt $listing.Count; $i++) {
    if ($listing[$i].Trim() -eq 'The following Tests are available:') {
      $headerIndex = $i
      break
    }
  }
  if ($headerIndex -lt 0) {
    throw "WPF client tests 清单缺少标题行，无法校验分片完整性"
  }
  # Theory 用例带参数后缀，过滤表达式只作用于方法全名。
  $testNames = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
  for ($i = $headerIndex + 1; $i -lt $listing.Count; $i++) {
    if ($listing[$i] -match '^\s{4}\S') {
      [void]$testNames.Add(($listing[$i].Trim() -split '\(', 2)[0])
    }
  }
  if ($testNames.Count -lt 1) {
    throw "WPF client tests 清单为空，拒绝假绿"
  }

  $problems = [System.Collections.Generic.List[string]]::new()
  foreach ($testName in $testNames) {
    $matched = [System.Collections.Generic.List[string]]::new()
    foreach ($shardName in $clientShards.Keys) {
      if (Test-ClientShardMatch -Definition $clientShards[$shardName] -TestName $testName) {
        $matched.Add($shardName)
      }
    }
    if ($matched.Count -ne 1) {
      $problems.Add("$testName -> [$($matched -join ', ')]")
    }
  }
  if ($problems.Count -gt 0) {
    $problems | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
    throw "WPF client tests 有 $($problems.Count) 个测试未落入或重复落入分片（见上方列表，最多显示 20 条）"
  }
  Write-Host "WPF client tests 分片完整性校验通过：$($testNames.Count) 个测试方法各属一个分片"
}

if ($Shard -eq 'all' -or $clientShards.Contains($Shard)) {
  $clientFilter = if ($Shard -eq 'all') {
    $testFilter
  } else {
    "($testFilter)&($(Get-ClientShardFilter -Definition $clientShards[$Shard]))"
  }
  $clientLogFileName = if ($Shard -eq 'all') {
    'Hbpos.Client.Tests.trx'
  } else {
    "Hbpos.Client.Tests.$Shard.trx"
  }
  $clientDestination = if ($Shard -eq 'all') {
    $resultsRoot
  } else {
    Join-Path $resultsRoot $Shard
  }
  Invoke-TestProject `
    -Project $clientTestsProject `
    -Filter $clientFilter `
    -LogFileName $clientLogFileName `
    -Label "WPF client tests ($Shard)" `
    -Destination $clientDestination
}

if ($Shard -eq 'all' -or $Shard -eq 'ui') {
  $uiDestination = if ($Shard -eq 'all') {
    $resultsRoot
  } else {
    Join-Path $resultsRoot 'ui'
  }
  Invoke-TestProject `
    -Project 'apps/pos-wpf/tests/Hbpos.Client.UiTests/Hbpos.Client.UiTests.csproj' `
    -Filter $testFilter `
    -LogFileName 'Hbpos.Client.UiTests.trx' `
    -Label 'WPF UI tests' `
    -Destination $uiDestination

  # ui 分片同时承担不按类名分片的小型检查：RemoteStatus 测试与 client 分片完整性。
  Invoke-TestProject `
    -Project 'apps/pos-wpf/tests/Hbpos.RemoteStatus.Tests/Hbpos.RemoteStatus.Tests.csproj' `
    -Filter $testFilter `
    -LogFileName 'Hbpos.RemoteStatus.Tests.trx' `
    -Label 'WPF RemoteStatus tests' `
    -Destination $uiDestination
  Assert-ClientShardCoverage
}
