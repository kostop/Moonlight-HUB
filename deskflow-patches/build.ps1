[CmdletBinding()]
param(
    # QtRoot：Qt 6.7+ 的 MSVC x64 SDK，含 bin/include/lib。
    [Parameter(Mandatory = $true)][string]$QtRoot,
    # OpenSslRoot：OpenSSL SDK，含 include/openssl 和 lib。
    [Parameter(Mandatory = $true)][string]$OpenSslRoot,
    # DependencySource：运行 DLL 来源，含 OpenSSL 及 Qt SDK 未安装的同版本可选模块。
    [Parameter(Mandatory = $true)][string]$DependencySource,
    # CMake：CMake 可执行文件；Ninja 需在 PATH 中。
    [string]$CMake = 'cmake',
    # PreparedSource：可选，已经应用本仓库补丁的构建源码；不读取独立安装的程序。
    [string]$PreparedSource,
    # BuildDir：仅用于本仓库随附核心的构建输出。
    [string]$BuildDir,
    # OutputDir：生成可移动的完整组件，供 bundle-runtime.ps1 使用。
    [string]$OutputDir,
    # Jobs：C++ 并行编译数量。
    [int]$Jobs = 6,
    # PrepareOnly：只取得并应用固定版本的源码补丁，不编译或运行任何程序。
    [switch]$PrepareOnly,
    # PackageOnly：复用已有核心及触摸组件产物，仅装配依赖、许可证与清单。
    [switch]$PackageOnly,
    # Test：运行不注入真实键鼠的回归。
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$hubRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $hubRoot 'artifacts'
if (!$BuildDir) { $BuildDir = Join-Path $artifacts 'deskflow-build' }
if (!$OutputDir) { $OutputDir = Join-Path $artifacts 'deskflow-runtime' }
$BuildDir = [IO.Path]::GetFullPath($BuildDir)
$OutputDir = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\')
$base = '760e3b99b00053647a96b405276bf614bd860075'

# Arguments：原样传给 git 的参数；返回字符串输出及退出码，stderr 本身不代表命令失败。
# WinPS 5 会把 clone 的正常进度转换为 ErrorRecord，必须先局部捕获再按退出码判断。
function Invoke-SourceGit([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    return [pscustomobject]@{ExitCode=$exitCode; Output=@($output | ForEach-Object {$_.ToString()})}
}

# Source/Patch：待检查的源码目录和补丁；Reverse：检查补丁是否已经应用。
function Test-SourcePatch([string]$Source, [string]$Patch, [switch]$Reverse) {
    $arguments = @('-C', $Source, 'apply', '--check')
    if ($Reverse) { $arguments += '--reverse' }
    $arguments += $Patch
    return (Invoke-SourceGit -Arguments $arguments).ExitCode -eq 0
}

# Name：Qt DLL 名；QtBin/DependencyDir：优先 SDK、其次运行依赖；ExpectedVersion：QtCore 文件版本。
# ReadVersion：文件版本查询边界，离线测试可使用内存元数据，生产默认读取真实 PE 版本。
function Resolve-QtRuntimeFile {
    param([string]$Name, [string]$QtBin, [string]$DependencyDir, [string]$ExpectedVersion,
        [scriptblock]$ReadVersion = { param($Path) (Get-Item -LiteralPath $Path).VersionInfo.FileVersion })
    $path = Join-Path $QtBin $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $path = Join-Path $DependencyDir $Name }
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Qt runtime DLL is missing: $Name" }
    $version = [string](& $ReadVersion $path)
    $version = $version.Trim()
    if (!$version) { throw "Qt runtime DLL has no file version: $Name" }
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) {
        throw "Qt runtime version mismatch: $Name has $version, expected Qt6Core $ExpectedVersion"
    }
    return [pscustomobject]@{Path=$path; Version=$version}
}

# Patches：按应用顺序排列的补丁；返回需要核验的全部原始及目标文件路径。
function Get-SourcePatchPaths([IO.FileInfo[]]$Patches) {
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($patch in $Patches) {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($patch.FullName), '(?m)^(?:--- a/|\+\+\+ b/)([^\r\n]+)')) {
            $relative = $match.Groups[1].Value
            if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[/\\]') -contains '..') {
                throw "Unsafe source path in patch: $relative"
            }
            [void]$paths.Add($relative)
        }
    }
    return @($paths | Sort-Object)
}

# Source/Patches：仅本脚本生成的源码缓存及补丁；验证完整序列和所有相关文件，避免相互覆盖的补丁被重复应用。
function Test-SourceReceipt([string]$Source, [IO.FileInfo[]]$Patches) {
    $path = Join-Path $Source '.moonlight-hub-patches.json'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try {
        $receipt = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($receipt.format -ne 1 -or @($receipt.patches.PSObject.Properties).Count -ne $Patches.Count) { return $false }
        foreach ($patch in $Patches) {
            if ($receipt.patches.PSObject.Properties[$patch.Name].Value -ne (Get-FileHash -LiteralPath $patch.FullName).Hash) { return $false }
        }
        foreach ($relative in Get-SourcePatchPaths -Patches $Patches) {
            $entry = $receipt.files.PSObject.Properties[$relative]
            if (!$entry) { return $false }
            $file = Join-Path $Source $relative
            if ($entry.Value) {
                if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.Value) { return $false }
            } elseif (Test-Path -LiteralPath $file) { return $false }
        }
        return $true
    } catch { return $false }
}

# Source/Patches：验证旧缓存记录是当前有序补丁列表的完整前缀；内容变化时禁止重放或覆盖已有源码。
function Get-CachedPatchPrefixLength([string]$Source, [IO.FileInfo[]]$Patches) {
    $path = Join-Path $Source '.moonlight-hub-patches.json'
    try { $receipt = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw 'Invalid cached patch receipt; use a separate clean generated source directory.' }
    $count = @($receipt.patches.PSObject.Properties).Count
    if ($receipt.format -ne 1 -or $count -gt $Patches.Count) {
        throw 'Cached patch prefix changed; refusing to overwrite the generated source.'
    }
    $prefix = @($Patches | Select-Object -First $count)
    foreach ($patch in $prefix) {
        $entry = $receipt.patches.PSObject.Properties[$patch.Name]
        if (!$entry -or $entry.Value -ne (Get-FileHash -LiteralPath $patch.FullName).Hash) {
            throw "Cached patch prefix changed: $($patch.Name); refusing to overwrite the generated source."
        }
    }
    if (!(Test-SourceReceipt -Source $Source -Patches $prefix)) {
        throw 'Cached patched source changed; refusing to apply additional patches over local edits.'
    }
    return $count
}

# Source/Patches：应用完成后的生成缓存及补丁；记录内容哈希，只有内容未变时才允许跳过整组补丁。
function Write-SourceReceipt([string]$Source, [IO.FileInfo[]]$Patches) {
    $patchesByName = @{}
    foreach ($patch in $Patches) { $patchesByName[$patch.Name] = (Get-FileHash -LiteralPath $patch.FullName).Hash }
    $files = @{}
    foreach ($relative in Get-SourcePatchPaths -Patches $Patches) {
        $file = Join-Path $Source $relative
        $files[$relative] = if (Test-Path -LiteralPath $file -PathType Leaf) { (Get-FileHash -LiteralPath $file).Hash } else { '' }
    }
    @{format=1;patches=$patchesByName;files=$files} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Source '.moonlight-hub-patches.json') -Encoding UTF8
}

# Source/Patches：验证旧缓存已应用完整补丁序列；临时索引只计算期望内容，不改变真实源码或 Git 索引。
function Test-SourcePatchSeries([string]$Source, [IO.FileInfo[]]$Patches) {
    $temporaryIndex = Join-Path $Source ('.moonlight-patch-index-' + [guid]::NewGuid().ToString('N'))
    $previousIndex = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $temporaryIndex
        if ((Invoke-SourceGit -Arguments @('-C', $Source, 'read-tree', 'HEAD')).ExitCode -ne 0) { return $false }
        foreach ($patch in $Patches) {
            if ((Invoke-SourceGit -Arguments @('-C', $Source, 'apply', '--cached', $patch.FullName)).ExitCode -ne 0) { return $false }
        }
        $arguments = @('-C', $Source, 'diff', '--quiet', '--') + @(Get-SourcePatchPaths -Patches $Patches)
        return (Invoke-SourceGit -Arguments $arguments).ExitCode -eq 0
    } finally {
        $env:GIT_INDEX_FILE = $previousIndex
        # 临时索引名称在当前生成源码内固定构造，只移除本次创建的叶文件。
        if (Test-Path -LiteralPath $temporaryIndex -PathType Leaf) { Remove-Item -LiteralPath $temporaryIndex -Force }
        if (Test-Path -LiteralPath ($temporaryIndex + '.lock') -PathType Leaf) { Remove-Item -LiteralPath ($temporaryIndex + '.lock') -Force }
    }
}

if (!$PreparedSource) {
    $PreparedSource = Join-Path $artifacts 'deskflow-upstream'
    if (!(Test-Path -LiteralPath $PreparedSource)) {
        $clone = Invoke-SourceGit -Arguments @('clone', '-c', 'core.symlinks=false', '-c', 'core.autocrlf=false', '--depth', '1', '--branch',
            'v1.26.0', '--single-branch', 'https://github.com/deskflow/deskflow.git', $PreparedSource)
        $clone.Output | ForEach-Object { Write-Output $_ }
        if ($clone.ExitCode -ne 0) { throw 'Could not download the pinned upstream source.' }
    }
    $revision = Invoke-SourceGit -Arguments @('-C', $PreparedSource, 'rev-parse', 'HEAD')
    if ($revision.ExitCode -ne 0 -or $revision.Output.Count -eq 0) { throw 'Could not read the pinned source revision.' }
    $actualBase = $revision.Output[-1].Trim()
    if ($actualBase -ne $base) { throw 'Unexpected upstream revision; use a clean v1.26.0 source.' }
    $patches = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.patch' -File | Sort-Object Name)
    $receiptPath = Join-Path $PreparedSource '.moonlight-hub-patches.json'
    $prefixLength = 0
    $haveReceipt = Test-Path -LiteralPath $receiptPath -PathType Leaf
    if ($haveReceipt) { $prefixLength = Get-CachedPatchPrefixLength -Source $PreparedSource -Patches $patches }
    if (!$haveReceipt -or $prefixLength -lt $patches.Count) {
        if ($haveReceipt -or !(Test-SourcePatchSeries -Source $PreparedSource -Patches $patches)) {
            foreach ($patch in @($patches | Select-Object -Skip $prefixLength)) {
                if (Test-SourcePatch -Source $PreparedSource -Patch $patch.FullName -Reverse) { continue }
                if (!(Test-SourcePatch -Source $PreparedSource -Patch $patch.FullName)) {
                    throw "Patch does not apply: $($patch.Name)"
                }
                $applied = Invoke-SourceGit -Arguments @('-C', $PreparedSource, 'apply', $patch.FullName)
                $applied.Output | ForEach-Object { Write-Output $_ }
                if ($applied.ExitCode -ne 0) { throw "Patch failed: $($patch.Name)" }
            }
        }
        Write-SourceReceipt -Source $PreparedSource -Patches $patches
    }
}
$PreparedSource = (Resolve-Path -LiteralPath $PreparedSource).Path
if ($PrepareOnly) {
    Write-Output "Prepared Hub Deskflow source: $PreparedSource"
    return
}
if (!$PackageOnly) {
    & (Join-Path $PreparedSource 'tools\build-pointer-core.ps1') -QtRoot $QtRoot -OpenSslRoot $OpenSslRoot `
        -BuildDir $BuildDir -CMake $CMake -Jobs $Jobs -TestRouting:$Test
    if (!$?) { throw 'Bundled Deskflow core build failed.' }
    & (Join-Path $hubRoot 'native\deskflow-touch\build.ps1') -OutputDir $OutputDir
    if (!$?) { throw 'Hub touch adapter build failed.' }
}
$native = Join-Path $OutputDir 'native'
New-Item -ItemType Directory -Force -Path $native | Out-Null
Copy-Item -LiteralPath (Join-Path $BuildDir 'bin\deskflow-core.exe') -Destination $native -Force
$qtBin = Join-Path $QtRoot 'bin'
$qtCore = Resolve-QtRuntimeFile -Name 'Qt6Core.dll' -QtBin $qtBin -DependencyDir $DependencySource
foreach ($name in @('Qt6Core.dll', 'Qt6Gui.dll', 'Qt6Network.dll', 'Qt6Svg.dll', 'Qt6Widgets.dll')) {
    $runtime = Resolve-QtRuntimeFile -Name $name -QtBin $qtBin -DependencyDir $DependencySource -ExpectedVersion $qtCore.Version
    Copy-Item -LiteralPath $runtime.Path -Destination $native -Force
}
foreach ($name in @('libcrypto-3-x64.dll', 'libssl-3-x64.dll')) {
    Copy-Item -LiteralPath (Join-Path $DependencySource $name) -Destination $native -Force
}
$redistRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Redist\MSVC'
$redist = Get-ChildItem -LiteralPath $redistRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+(?:\.\d+)?$' -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\Microsoft.VC143.CRT') -PathType Container) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$redist) { throw 'MSVC redistributable runtime is missing.' }
$crt = Join-Path $redist.FullName 'x64\Microsoft.VC143.CRT'
Get-ChildItem -LiteralPath $crt -Filter '*.dll' -File | Copy-Item -Destination $native -Force
Copy-Item -LiteralPath (Join-Path $PreparedSource 'LICENSE') -Destination $OutputDir -Force
$licenseSource = Join-Path $PreparedSource 'LICENSES'
Get-ChildItem -LiteralPath $licenseSource -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($licenseSource.Length).TrimStart('\')
    $licenseTarget = Join-Path (Join-Path $OutputDir 'LICENSES') $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $licenseTarget) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $licenseTarget -Force
}
# 随附 Qt/OpenSSL 的原始许可和固定版本来源，不依赖开发机 SDK 是否附带许可文件。
$dependencyLicenses = Join-Path $hubRoot 'native\deskflow-touch\licenses'
$runtimeLicenses = Join-Path $OutputDir 'LICENSES'
New-Item -ItemType Directory -Force -Path $runtimeLicenses | Out-Null
Get-ChildItem -LiteralPath $dependencyLicenses -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $runtimeLicenses $_.Name) -Force
}
if ($Test) {
    foreach ($name in @('test-cursor-warp', 'test-local-key-release', 'test-mouse-return', 'test-key-handoff', 'test-desktop-focus')) {
        & (Join-Path $PreparedSource "tools\$name.ps1")
        if (!$?) { throw "Offline regression failed: $name" }
    }
    & (Join-Path $PreparedSource 'tools\test-mouse-handoff.ps1') -QtRoot $QtRoot -CoreBuildDir $BuildDir
    if (!$?) { throw 'Mouse handoff regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-modifier-reconcile.ps1') -QtRoot $QtRoot -CoreBuildDir $BuildDir
    if (!$?) { throw 'Key reconciliation regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-key-refresh.ps1') -QtRoot $QtRoot -CoreBuildDir $BuildDir
    if (!$?) { throw 'Receiver key ownership refresh regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-mouse-held.ps1') -QtRoot $QtRoot
    if (!$?) { throw 'Held mouse screen-switch regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-mouse-recovery.ps1') -QtRoot $QtRoot
    if (!$?) { throw 'Failed screen-switch recovery regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-menu-handoff.ps1') -QtRoot $QtRoot -CoreBuildDir $BuildDir
    if (!$?) { throw 'Source menu handoff regression failed.' }
    & (Join-Path $PreparedSource 'tools\test-receiver-menu.ps1') -QtRoot $QtRoot -CoreBuildDir $BuildDir
    if (!$?) { throw 'Receiver menu handoff regression failed.' }
}
$patchHashes = @{}
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.patch' -File | ForEach-Object {
    $patchHashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
$fileHashes = @{}
$runtimeFiles = @('deskflow-core.exe', 'deskflow_touch_filter.dll', 'moonlight-touch-keyboard-release.exe',
    'moonlight-touch-bridge.exe', 'LICENSE')
foreach ($relative in $runtimeFiles) {
    $fileHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $OutputDir $relative)).Hash
}
Get-ChildItem -LiteralPath $native -File | Where-Object {
    $_.Name -eq 'deskflow-core.exe' -or $_.Extension -eq '.dll'
} | ForEach-Object {
    $fileHashes['native\' + $_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
Get-ChildItem -LiteralPath (Join-Path $OutputDir 'LICENSES') -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring([IO.Path]::GetFullPath($OutputDir).Length).TrimStart('\')
    $fileHashes[$relative] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
@{component='Moonlight Hub bundled Deskflow'; upstream='v1.26.0'; base=$base;
    coreSha256=(Get-FileHash -LiteralPath (Join-Path $native 'deskflow-core.exe')).Hash;
    patches=$patchHashes; files=$fileHashes} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $OutputDir 'runtime-manifest.json') -Encoding UTF8
Write-Output "Built Hub Deskflow component: $OutputDir"
