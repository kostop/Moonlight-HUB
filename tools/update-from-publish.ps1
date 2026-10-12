[CmdletBinding()]
param(
    # PublishDirectory：已经编译验证的发布目录。
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    # InstallDirectory：现有 Hub 安装目录。
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    # UpdateDirectory：本次更新的备份、临时程序和日志目录。
    [Parameter(Mandatory = $true)][string]$UpdateDirectory
)
$ErrorActionPreference = 'Stop'
$PublishDirectory = (Get-Item -LiteralPath $PublishDirectory).FullName
$InstallDirectory = (Get-Item -LiteralPath $InstallDirectory).FullName
$UpdateDirectory = [IO.Path]::GetFullPath($UpdateDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PublishDirectory 'MoonlightHub.exe'))) { throw 'Publish executable missing.' }
if (!(Test-Path -LiteralPath (Join-Path $InstallDirectory 'MoonlightHub.exe'))) { throw 'Installed executable missing.' }
if ($PublishDirectory -eq $InstallDirectory) { throw 'Publish and install directories must differ.' }
$stage = Join-Path $UpdateDirectory 'staging'
$backup = Join-Path $UpdateDirectory 'backup'
New-Item -ItemType Directory -Path $stage,$backup -Force | Out-Null

# source/destination：仅复制两个明确目录的文件，确认每个目标都在指定目录内。
function Copy-Tree([string]$source, [string]$destination, [switch]$ApplicationFiles) {
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\')
        if ($ApplicationFiles -and $relative -match '^(tools|drivers|sunshine)\\') { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
        if (!$target.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid copy destination.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

# executable：启动新实例，先接住 VDD 心跳，再等待本桌面的旧实例退出。
function Replace-Hub([string]$executable) {
    $process = Start-Process -FilePath $executable -ArgumentList '--minimized','--replace' -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden -PassThru
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 400
        $process.Refresh()
        if ($process.HasExited) { throw ('New hub exited: ' + $process.ExitCode) }
        $others = @(Get-Process MoonlightHub -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $process.Id -and $_.SessionId -eq $session })
    } while ($others.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($others.Count -gt 0) { throw 'Old hub did not release its files.' }
    Start-Sleep -Seconds 2
    return $process.Id
}

try {
    Get-ChildItem -LiteralPath $InstallDirectory -File | Copy-Item -Destination $backup -Force
    Copy-Tree $InstallDirectory $stage
    Copy-Tree $PublishDirectory $stage -ApplicationFiles
    $first = Replace-Hub (Join-Path $stage 'MoonlightHub.exe')
    Copy-Tree $PublishDirectory $InstallDirectory -ApplicationFiles
    $last = Replace-Hub (Join-Path $InstallDirectory 'MoonlightHub.exe')
    @{ ok=$true; pid=$last; install=$InstallDirectory; backup=$backup } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $UpdateDirectory 'result.json') -Encoding UTF8
} catch {
    @{ ok=$false; error=$_.Exception.Message; backup=$backup } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $UpdateDirectory 'result.json') -Encoding UTF8
    throw
}
