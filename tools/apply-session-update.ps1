[CmdletBinding()]
param(
    # Stage：已经解压好的新版 Hub 目录。
    [string]$Stage,
    # Target：正在使用的 Hub 目录。
    [string]$Target
)

$Stage = if ($Stage) { $Stage } else { $PSScriptRoot }
$Target = if ($Target) { $Target } else { Split-Path $Stage -Parent }
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:LOCALAPPDATA 'MoonlightHub\logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$log = Join-Path $logDir ('update-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
Start-Transcript -LiteralPath $log -Force | Out-Null
try {
    $stageExe = Join-Path $Stage 'MoonlightHub.exe'
    $targetExe = Join-Path $Target 'MoonlightHub.exe'
    if (!(Test-Path -LiteralPath $stageExe)) { throw "找不到 $stageExe" }
    if (!(Test-Path -LiteralPath $targetExe)) { throw "找不到 $targetExe" }

    # 先让 staging 实例在用户桌面会话里接管 VDD 和串流，再覆盖正式目录。
    $first = Start-Process -FilePath $stageExe -ArgumentList '--minimized', '--replace' -WorkingDirectory $Stage -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        if (!(Get-Process -Id $first.Id -ErrorAction SilentlyContinue)) { throw 'staging 实例提前退出' }
        if (@(Get-Process MoonlightHub -ErrorAction SilentlyContinue | Where-Object Id -ne $first.Id).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 3

    # 正在运行的 Sunshine 会锁住自己的文件，留给下次更新；主程序与运行库必须全部换新。
    $failed = @()
    Get-ChildItem -LiteralPath $Stage -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Stage.Length).TrimStart('\')
        $destination = Join-Path $Target $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        try { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force } catch { $failed += $relative }
    }

    $second = Start-Process -FilePath $targetExe -ArgumentList '--minimized', '--replace' -WorkingDirectory $Target -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        if (!(Get-Process -Id $second.Id -ErrorAction SilentlyContinue)) { throw '正式目录实例提前退出' }
        if (@(Get-Process MoonlightHub -ErrorAction SilentlyContinue | Where-Object Id -ne $second.Id).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    $version = (Get-Item -LiteralPath $targetExe).VersionInfo.FileVersion
    "update ok pid=$($second.Id) version=$version failed=$($failed -join ',')"
}
catch {
    "update failed: $($_.Exception.Message)"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
