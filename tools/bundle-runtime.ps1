[CmdletBinding()]
param(
    # 目标目录（dist / dist-staging / release\build），脚本会在其中创建 sunshine\ 与 drivers\
    [Parameter(Mandatory = $true)][string]$Target,
    # 定制版 Sunshine 的运行目录（含 sunshine.exe、assets\、tools\、zlib1.dll）
    [string]$SunshineSource = 'D:\YingYong\Sunshine',
    # Parsec 官方驱动安装包（ParsecVDisplay 附带的 driver\parsec-vdd-setup.exe）
    [string]$DriverSetup = 'D:\YingYong\ParsecVDisplay\driver\parsec-vdd-setup.exe',
    # MSYS2 ucrt64 的 bin 目录，用于 strip 掉 sunshine.exe 的调试段（225 MB -> 45 MB）；找不到就原样复制
    [string]$Ucrt64Bin = 'D:\tmp\sunshine-msys2\msys64\ucrt64\bin'
)

# 把 Moonlight Hub 依赖的运行时嵌入到发布目录：
#   <Target>\sunshine\   定制版 Sunshine（去掉调试符号），配置不放在这里（运行时写到 %LOCALAPPDATA%\MoonlightHub\sunshine）
#   <Target>\drivers\    parsec-vdd-setup.exe，Hub 检测不到驱动时用它静默安装
$ErrorActionPreference = 'Stop'

$sunshineDir = Join-Path $Target 'sunshine'
$driversDir = Join-Path $Target 'drivers'
New-Item -ItemType Directory -Force $sunshineDir, $driversDir | Out-Null

$exe = Join-Path $SunshineSource 'sunshine.exe'
if (Test-Path $exe) {
    $strip = Join-Path $Ucrt64Bin 'strip.exe'
    $srcInfo = Get-Item $exe
    $dst = Join-Path $sunshineDir 'sunshine.exe'
    $stamp = Join-Path $sunshineDir '.source-stamp'
    $expected = '{0}|{1}' -f $srcInfo.Length, $srcInfo.LastWriteTimeUtc.Ticks
    if ((Test-Path $dst) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $expected)) {
        Write-Output "sunshine.exe 未变化，跳过"
    } elseif (Test-Path $strip) {
        & $strip --strip-debug -o $dst $exe
        if ($LASTEXITCODE -ne 0) { throw "strip 失败 ($LASTEXITCODE)" }
        Set-Content -Path $stamp -Value $expected -Encoding ascii
        Write-Output ("sunshine.exe: {0:N1} MB -> {1:N1} MB (strip --strip-debug)" -f ($srcInfo.Length / 1MB), ((Get-Item $dst).Length / 1MB))
    } else {
        Copy-Item $exe $dst -Force
        Set-Content -Path $stamp -Value $expected -Encoding ascii
        Write-Output ("sunshine.exe: {0:N1} MB (未 strip，找不到 {1})" -f ($srcInfo.Length / 1MB), $strip)
    }
    foreach ($name in 'zlib1.dll') {
        $f = Join-Path $SunshineSource $name
        if (Test-Path $f) { Copy-Item $f (Join-Path $sunshineDir $name) -Force }
    }
    foreach ($dir in 'assets', 'tools') {
        $s = Join-Path $SunshineSource $dir
        if (Test-Path $s) {
            $d = Join-Path $sunshineDir $dir
            if (Test-Path $d) { Remove-Item $d -Recurse -Force }
            Copy-Item $s $d -Recurse -Force
        }
    }
    # 不复制 config\（用户数据）与 scripts\（安装脚本），Hub 运行时不需要
    @"
Sunshine runtime bundled with Moonlight Hub.
Build: custom Sunshine 2025.924 (windows_client_pre_rotation, independent touch, hub pairing logs) — see sunshine-patches/ in the repository.
The executable was stripped of debug symbols; behaviour is identical to the full build.
Configuration lives in %LOCALAPPDATA%\MoonlightHub\sunshine\ (instance 1: config\, others: instances\<n>\).
"@ | Set-Content -Path (Join-Path $sunshineDir 'HUB-README.txt') -Encoding utf8
    Write-Output ("已嵌入 Sunshine -> {0} ({1:N0} MB)" -f $sunshineDir, ((Get-ChildItem $sunshineDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
} else {
    Write-Warning "找不到 $exe，未嵌入 Sunshine（Hub 将使用设置里指定的 sunshine.exe）"
}

if (Test-Path $DriverSetup) {
    Copy-Item $DriverSetup (Join-Path $driversDir 'parsec-vdd-setup.exe') -Force
    $sig = Get-AuthenticodeSignature (Join-Path $driversDir 'parsec-vdd-setup.exe')
    Write-Output ("已嵌入驱动安装包 parsec-vdd-setup.exe (签名 {0}: {1})" -f $sig.Status, ($sig.SignerCertificate.Subject -split ',')[0])
} else {
    Write-Warning "找不到 $DriverSetup，未嵌入驱动安装包"
}
