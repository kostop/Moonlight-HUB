[CmdletBinding()]
param(
    # PublishDirectory：已经编译验证的发布目录。
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    # InstallDirectory：现有 Hub 安装目录。
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    # UpdateDirectory：本次更新的备份、临时程序和日志目录。
    [Parameter(Mandatory = $true)][string]$UpdateDirectory,
    # DeskflowWaitSeconds：等待暂存 Hub 接管旧核心的最长时间，不强制结束任何进程。
    [ValidateRange(1, 900)][int]$DeskflowWaitSeconds = 180
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

# 只需查询有限进程信息，SYSTEM 客户端也不需要读取其内存或命令行。
if (!('MoonlightHubUpdate.CoreImage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace MoonlightHubUpdate {
 public static class CoreImage {
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static string Read(int id) {
   var handle=OpenProcess(0x1000, false, id);
   if(handle==IntPtr.Zero) return null;
   try { var path=new StringBuilder(32768); int length=path.Capacity;
    return QueryFullProcessImageName(handle,0,path,ref length) ? path.ToString() : null;
   } finally { CloseHandle(handle); }
  }
 }
}
'@
}

# 返回当前 deskflow-core 的映像快照；已退出的竞态忽略，仍在运行但读不到映像的进程保留为未知。
function Read-DeskflowCoreImages {
    foreach ($process in Get-Process deskflow-core -ErrorAction SilentlyContinue) {
        try {
            $path = [MoonlightHubUpdate.CoreImage]::Read($process.Id)
            if (!$path) {
                try { if ($process.HasExited) { continue } } catch { }
            }
            [pscustomobject]@{Id=$process.Id; Path=$path}
        } finally { $process.Dispose() }
    }
}

# Processes/RuntimeDirectory：进程快照和即将覆盖的组件目录；未知路径不能证明已经释放文件。
function Get-BlockingDeskflowCores([object[]]$Processes, [string]$RuntimeDirectory) {
    $prefix = [IO.Path]::GetFullPath($RuntimeDirectory).TrimEnd('\') + '\'
    foreach ($process in $Processes) {
        if ([string]::IsNullOrWhiteSpace($process.Path)) { $process; continue }
        try {
            if (![IO.Path]::IsPathRooted($process.Path) -or
                [IO.Path]::GetFullPath($process.Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $process
            }
        } catch { $process }
    }
}

# InstallDirectory/TimeoutSeconds：只等待旧安装内的核心退出或迁移，不结束核心或用户应用。
function Wait-InstalledDeskflowRelease([string]$InstallDirectory, [int]$TimeoutSeconds = 180) {
    $runtime = Join-Path $InstallDirectory 'deskflow'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastNotice = ''
    do {
        $blocked = @(Get-BlockingDeskflowCores -Processes @(Read-DeskflowCoreImages) -RuntimeDirectory $runtime)
        if ($blocked.Count -eq 0) { return }
        $notice = ($blocked | ForEach-Object {
            'pid=' + $_.Id + ' path=' + $(if ($_.Path) {$_.Path} else {'unknown'})
        }) -join '; '
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Deskflow component files were not overwritten: timed out waiting for the staged Hub to migrate cores from $runtime. $notice"
        }
        if ($notice -ne $lastNotice) {
            Write-Output "Waiting for installed Deskflow cores to migrate: $notice"
            $lastNotice = $notice
        }
        Start-Sleep -Milliseconds 500
    } while ($true)
}

try {
    Get-ChildItem -LiteralPath $InstallDirectory -File | Copy-Item -Destination $backup -Force
    Copy-Tree $InstallDirectory $stage
    Copy-Tree $PublishDirectory $stage -ApplicationFiles
    $first = Replace-Hub (Join-Path $stage 'MoonlightHub.exe')
    Wait-InstalledDeskflowRelease -InstallDirectory $InstallDirectory -TimeoutSeconds $DeskflowWaitSeconds
    Copy-Tree $PublishDirectory $InstallDirectory -ApplicationFiles
    $last = Replace-Hub (Join-Path $InstallDirectory 'MoonlightHub.exe')
    @{ ok=$true; pid=$last; install=$InstallDirectory; backup=$backup } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $UpdateDirectory 'result.json') -Encoding UTF8
} catch {
    @{ ok=$false; error=$_.Exception.Message; backup=$backup } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $UpdateDirectory 'result.json') -Encoding UTF8
    throw
}
