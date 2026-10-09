[CmdletBinding()]
param(
    # SkipBuild: 直接用已存在的 dist-staging 发布产物。
    [switch]$SkipBuild,
    # 内部使用：已提权的第二阶段
    [switch]$Elevated
)

# 无中断更新 Moonlight Hub：
#  1) dotnet publish -> dist-staging，并把 Sunshine / 驱动安装包嵌入（tools\bundle-runtime.ps1）
#  2) 从 dist-staging 以 --replace 启动新实例（新实例先接管 VDD keep-alive，再让旧实例退出，虚拟屏与 Sunshine 会话不中断）
#  3) 把 dist-staging 覆盖到 dist，再从 dist 以 --replace 启动一次，最后删除 dist-staging
# Hub 以管理员权限运行，所以脚本先把自己提权（ConsentPromptBehaviorAdmin=0 时静默），输出写到日志文件。
$ErrorActionPreference = 'Stop'
$hubRoot = Split-Path -Parent $PSScriptRoot
$src = Join-Path $hubRoot 'src'
$dist = Join-Path $hubRoot 'dist'
$staging = Join-Path $hubRoot 'dist-staging'
$logFile = Join-Path $env:LOCALAPPDATA 'MoonlightHub\logs\update-hub.log'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $SkipBuild -and -not $Elevated) {
    # 编译不需要管理员权限，在当前会话完成，输出直接可见
    Write-Output "== 发布到 $staging =="
    & dotnet publish (Join-Path $src 'MoonlightHub.csproj') -c Release -f net9.0-windows -r win-x64 --self-contained false -o $staging -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 ($LASTEXITCODE)" }
    & dotnet build-server shutdown | Out-Null
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'bundle-runtime.ps1') -Target $staging
}

if (-not $isAdmin) {
    Write-Output '== 以管理员权限执行热替换（输出见 ' + $logFile + '） =='
    if (Test-Path $logFile) { Remove-Item $logFile -Force }
    $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-SkipBuild', '-Elevated')
    $p.WaitForExit()
    if (Test-Path $logFile) { Get-Content $logFile }
    if ($p.ExitCode -ne 0) { throw "提权阶段失败 ($($p.ExitCode))" }
    return
}

if ($Elevated) { Start-Transcript -Path $logFile -Force | Out-Null }
try {
    function Start-Hub([string]$dir) {
        $exe = Join-Path $dir 'MoonlightHub.exe'
        $p = Start-Process -FilePath $exe -ArgumentList '--minimized', '--replace' -WorkingDirectory $dir -PassThru
        Write-Output ("已从 {0} 启动 pid={1}" -f $dir, $p.Id)
        return $p
    }

    function Wait-OtherHubsExit([int]$keepPid, [int]$timeoutSec = 30) {
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        do {
            $others = @(Get-Process MoonlightHub -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $keepPid })
            if ($others.Count -eq 0) { return $true }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        return $false
    }

    if (-not (Test-Path (Join-Path $staging 'MoonlightHub.exe'))) { throw "找不到 $staging\MoonlightHub.exe，请先不带 -SkipBuild 运行" }

    Write-Output '== 第一次热替换：staging 接管 =='
    $p1 = Start-Hub $staging
    if (-not (Wait-OtherHubsExit $p1.Id)) { Write-Warning '旧实例未在 30 秒内退出' }
    Start-Sleep -Seconds 3

    Write-Output "== 覆盖 $dist =="
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    # sunshine.exe 可能正被实例占用：先复制其余文件，再单独处理被占用的文件
    $failed = @()
    Get-ChildItem $staging -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($staging.Length).TrimStart('\')
        $dst = Join-Path $dist $rel
        New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
        try { Copy-Item $_.FullName $dst -Force } catch { $failed += $rel }
    }
    if ($failed.Count -gt 0) { Write-Warning ("以下文件被占用，未更新（实例重启后再运行一次即可）: " + ($failed -join ', ')) }

    Write-Output '== 第二次热替换：回到 dist =='
    $p2 = Start-Hub $dist
    if (-not (Wait-OtherHubsExit $p2.Id)) { Write-Warning 'staging 实例未在 30 秒内退出' }
    Start-Sleep -Seconds 2
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output ("完成。当前 Hub pid={0}，路径 {1}" -f $p2.Id, $dist)
}
finally {
    if ($Elevated) { Stop-Transcript | Out-Null }
}
