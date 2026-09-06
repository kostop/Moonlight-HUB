[CmdletBinding()]
param(
    # SkipBuild: 直接用已存在的 dist-staging 发布产物。
    [switch]$SkipBuild
)

# 无中断更新 Moonlight Hub：
#  1) dotnet publish -> dist-staging
#  2) 从 dist-staging 以 --replace 启动新实例（新实例先接管 VDD keep-alive，再让旧实例退出，虚拟屏与 Sunshine 会话不中断）
#  3) 把 dist-staging 覆盖到 dist，再从 dist 以 --replace 启动一次，最后删除 dist-staging
$ErrorActionPreference = 'Stop'
$hubRoot = Split-Path -Parent $PSScriptRoot
$src = Join-Path $hubRoot 'src'
$dist = Join-Path $hubRoot 'dist'
$staging = Join-Path $hubRoot 'dist-staging'

if (-not $SkipBuild) {
    Write-Output "== 发布到 $staging =="
    & dotnet publish (Join-Path $src 'MoonlightHub.csproj') -c Release -r win-x64 --self-contained false -o $staging -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 ($LASTEXITCODE)" }
    & dotnet build-server shutdown | Out-Null
}

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

Write-Output '== 第一次热替换：staging 接管 =='
$p1 = Start-Hub $staging
if (-not (Wait-OtherHubsExit $p1.Id)) { Write-Warning '旧实例未在 30 秒内退出' }
Start-Sleep -Seconds 3

Write-Output "== 覆盖 $dist =="
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $dist -Recurse -Force

Write-Output '== 第二次热替换：回到 dist =='
$p2 = Start-Hub $dist
if (-not (Wait-OtherHubsExit $p2.Id)) { Write-Warning 'staging 实例未在 30 秒内退出' }
Start-Sleep -Seconds 2
Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ("完成。当前 Hub pid={0}，路径 {1}" -f $p2.Id, $dist)
