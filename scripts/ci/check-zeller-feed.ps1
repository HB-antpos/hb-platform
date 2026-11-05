<#
  中文注释：还原 WPF 之前先确认 Zeller 私有源凭据可用。

  WPF 客户端引用的 Zeller.PaymentsSdk 只能从 Zeller 私有源下载（apps/pos-wpf/NuGet.config）。
  凭据不对时 NuGet 只报 NU1301「403」，看不出是仓库 Secret 的问题（2026-11-05 实际遇到过：Secret 里存的令牌被截断了）。
  这里先用同一组凭据请求一次服务索引，失败时直接说明。只打印状态码和长度，不打印凭据。

  凭据来自环境变量 ZELLER_NUGET_USERNAME / ZELLER_NUGET_TOKEN（工作流从同名仓库 Secret 传入）。
  网络异常或私有源返回其它状态时只给警告、不拦截，交给后面的还原去报错。
#>
[CmdletBinding()]
param(
  [string]$FeedUrl = 'https://zeller.jfrog.io/artifactory/api/nuget/v3/nuget/index.json',
  [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

$username = "$env:ZELLER_NUGET_USERNAME".Trim()
$token = "$env:ZELLER_NUGET_TOKEN".Trim()
if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrWhiteSpace($token)) {
  throw '缺少仓库 Secret ZELLER_NUGET_USERNAME / ZELLER_NUGET_TOKEN：无法从 Zeller 私有源下载 Zeller.PaymentsSdk。'
}

$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${username}:${token}"))
$status = $null
try {
  $response = Invoke-WebRequest -Uri $FeedUrl -Headers @{ Authorization = "Basic $basic" } -UseBasicParsing -TimeoutSec $TimeoutSeconds
  $status = [int]$response.StatusCode
}
catch {
  # Windows PowerShell 5.1 与 PowerShell 7 遇到非 2xx 都会抛异常，两者的 Response 都有 StatusCode。
  $failed = $_.Exception.Response
  if ($null -ne $failed) {
    $status = [int]$failed.StatusCode
  }
  else {
    Write-Warning "无法连接 Zeller 私有源（$($_.Exception.GetType().Name): $($_.Exception.Message)），跳过凭据检查，交给还原报错。"
    exit 0
  }
}

if ($status -ge 200 -and $status -lt 300) {
  Write-Host "Zeller 私有源凭据可用（HTTP $status）。"
  exit 0
}

if ($status -in 401, 403) {
  throw ("Zeller 私有源拒绝了仓库 Secret 里的凭据（HTTP $status；用户名长度 $($username.Length)，令牌长度 $($token.Length)）。" +
    '请重新设置 ZELLER_NUGET_USERNAME / ZELLER_NUGET_TOKEN：取值是 Zeller 开发者文档「Payments SDK - .NET」快速入门里的只读账号，' +
    '令牌是一整串很长的字符，复制时不要截断。')
}

Write-Warning "Zeller 私有源返回 HTTP $status，跳过凭据检查，交给还原报错。"
exit 0
