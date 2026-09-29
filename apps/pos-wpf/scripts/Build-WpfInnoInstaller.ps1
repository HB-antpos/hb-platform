#requires -Version 5.1

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot = '.artifacts\wpf-release',

    [string]$IsccPath,

    [string[]]$LegacyMsiProductCode = @(),

    [switch]$AllowNonCommercialBuild
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    if (!$childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside output root: $Child"
    }
}

function Find-Iscc {
    $candidates = @(
        $IsccPath,
        $env:INNO_SETUP_ISCC,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
    )

    foreach ($candidate in $candidates) {
        if (![string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "ISCC.exe not found. Install Inno Setup first: winget install --id JRSoftware.InnoSetup -e -s winget"
}

function Test-InnoCommercialLicense {
    $licensePath = 'HKCU:\Software\Jordan Russell\Inno Setup\License'
    $license = Get-ItemProperty -Path $licensePath -Name LicenseKey -ErrorAction SilentlyContinue
    return -not [string]::IsNullOrWhiteSpace($license.LicenseKey)
}

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}

$versionParts = $normalizedVersion.Split('.')
$versionInfoVersion = if ($versionParts.Count -eq 3) { "$normalizedVersion.0" } else { $normalizedVersion }

$outputRootFull = Resolve-FullPath $OutputRoot
$versionOutputRoot = Join-Path $outputRootFull $normalizedVersion
$publishDir = Join-Path $versionOutputRoot 'publish'
$projectPath = Join-Path $RepoRoot 'apps\pos-wpf\src\Hbpos.Client.Wpf\Hbpos.Client.Wpf.csproj'
$remoteStatusProjectPath = Join-Path $RepoRoot 'apps\pos-wpf\src\Hbpos.RemoteStatus\Hbpos.RemoteStatus.csproj'
$remoteSetupProjectPath = Join-Path $RepoRoot 'apps\pos-wpf\src\Hbpos.RemoteMaintenance.Setup\Hbpos.RemoteMaintenance.Setup.csproj'
$updaterProjectPath = Join-Path $RepoRoot 'apps\pos-wpf\src\Hbpos.Updater\Hbpos.Updater.csproj'
$innoScript = Join-Path $RepoRoot 'apps\pos-wpf\installer\inno\Hbpos.Client.Wpf.iss'
$iscc = Find-Iscc
# 中文注释：旧 MSI ProductCode 是卸载边界，构建期先校验，避免坏 GUID 进入门店安装包。
$legacyMsiProductCodePattern = '^(?:\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}|[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})$'
$legacyMsiProductCodes = foreach ($productCode in $LegacyMsiProductCode) {
    if ([string]::IsNullOrWhiteSpace($productCode)) {
        continue
    }

    $normalizedProductCode = $productCode.Trim()
    if ($normalizedProductCode -notmatch $legacyMsiProductCodePattern) {
        throw "Legacy MSI ProductCode must be a GUID: $normalizedProductCode"
    }

    $normalizedProductCode
}
$legacyMsiProductCodes = $legacyMsiProductCodes -join ';'

# 中文注释：生产构建必须激活 Inno 商业授权；本地烟测需要显式传入 -AllowNonCommercialBuild 才放行。
if (-not (Test-InnoCommercialLicense)) {
    if (!$AllowNonCommercialBuild) {
        throw "Inno Setup commercial license key is not active. Re-run with -AllowNonCommercialBuild only for local smoke builds."
    }

    Write-Warning "Inno Setup commercial license key is not active. Continuing because -AllowNonCommercialBuild was specified."
}

# 中文注释：清理版本专属 publish 目录，避免旧 DLL 残留进安装包。
Assert-ChildPath -Parent $outputRootFull -Child $publishDir
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $versionOutputRoot -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:HbposWpfAppVersion=$normalizedVersion `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

# 中文注释：更新进度窗口放在 updater 子目录；收银端更新时先把它复制到临时目录再运行，
# 这样安装器替换 Program Files 下的文件时不会被它占用。
$updaterPublishDir = Join-Path $publishDir 'updater'
dotnet publish $updaterProjectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:HbposWpfAppVersion=$normalizedVersion `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $updaterPublishDir

$updaterExe = Join-Path $updaterPublishDir 'Hbpos.Updater.exe'
if (!(Test-Path -LiteralPath $updaterExe)) {
    throw "Publish output is missing the update progress window: $updaterExe"
}

# 中文注释：WPF 必须通过 all-users Inno 安装到 Program Files，helper 与状态服务
# 才能从受保护目录运行。仅把 Setup DLL 放进 WPF 输出会导致运行时找不到可提权的 EXE。
$remotePublishRoot = Join-Path $versionOutputRoot 'remote-maintenance'
$remoteStatusPublishDir = Join-Path $remotePublishRoot 'agent'
$remoteSetupPublishDir = Join-Path $remotePublishRoot 'helper'
dotnet publish $remoteStatusProjectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableWindowsTargeting=true `
    -o $remoteStatusPublishDir
dotnet publish $remoteSetupProjectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableWindowsTargeting=true `
    -o $remoteSetupPublishDir

$statusExe = Join-Path $remoteStatusPublishDir 'Hbpos.RemoteStatus.exe'
$helperExe = Join-Path $remoteSetupPublishDir 'Hbpos.RemoteMaintenance.Setup.exe'
if (!(Test-Path -LiteralPath $statusExe) -or !(Test-Path -LiteralPath $helperExe)) {
    throw "Remote maintenance publish output is incomplete; expected self-contained agent and helper EXEs."
}
Copy-Item -LiteralPath $statusExe -Destination $publishDir -Force
Copy-Item -LiteralPath $helperExe -Destination $publishDir -Force

$mainExe = Join-Path $publishDir 'Hbpos.Client.Wpf.exe'
if (!(Test-Path -LiteralPath $mainExe)) {
    throw "Publish output is missing Hbpos.Client.Wpf.exe: $mainExe"
}

& $iscc `
    "/DAppVersion=$normalizedVersion" `
    "/DVersionInfoVersion=$versionInfoVersion" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$versionOutputRoot" `
    "/DLegacyMsiProductCodes=$legacyMsiProductCodes" `
    $innoScript

$installerPath = Join-Path $versionOutputRoot "Hbpos.Client.Wpf-$normalizedVersion-x64.exe"
if (!(Test-Path -LiteralPath $installerPath)) {
    throw "Inno output is missing: $installerPath"
}

$hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
$file = Get-Item -LiteralPath $installerPath

[pscustomobject]@{
    InstallerPath = $file.FullName
    FileSize = $file.Length
    Sha256 = $hash.Hash.ToLowerInvariant()
    InstallerType = 'exe'
    InstallerArguments = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS'
}
