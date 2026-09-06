[CmdletBinding()]
param(
    # Root: 要整理的 moonlight 文件夹。
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),

    # DryRun: 只打印计划，不移动任何文件。
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 目标目录结构（相对 Root）
$layout = [ordered]@{
    'scripts'                 = '仍在使用的 PowerShell/VBS/Python 脚本（显示拓扑、USB 网络、Moonlight 客户端辅助）'
    'scripts/sunshine'        = 'Sunshine 服务/配置/部署脚本与配置文件'
    'deskflow-touch'          = 'Deskflow 触摸/键鼠隔离相关：脚本、C++ 源码、DLL/EXE 与配置'
    'deskflow-touch/variants' = '触摸过滤器/启动器的历史编译变体'
    'deskflow-touch/obj'      = '编译中间文件'
    'deskflow-touch/source'   = 'deskflow 源码树'
    'tools/probes'            = 'Win32 探针程序源码/可执行文件'
    'tools/latency'           = '延迟测量脚本'
    'moonlight-client'        = 'Android Moonlight 客户端：APK、源码、ADB 工具、数据库/偏好'
    'sunshine/sources'        = 'Sunshine 源码树（2025.924 含预旋转/独立触摸补丁）'
    'sunshine/packages'       = 'Sunshine 便携包、安装器、解压包'
    'sunshine/builds'         = '本地编译产物'
    'archive/backups'         = '历史备份'
    'archive/latency-tests'   = '延迟测试产物（图片/CSV/JSON）'
    'archive/screenshots'     = '截图'
    'archive/ui-dumps'        = 'Moonlight UI 层级导出 (xml)'
    'archive/logs'            = '各种状态/结果日志'
    'archive/misc'            = '其他杂项'
    'shortcuts'               = '快捷方式'
}

$moves = [System.Collections.Generic.List[object]]::new()

function Plan {
    param([string]$Name, [string]$Dest)
    $src = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $src)) { return }
    $moves.Add([pscustomobject]@{ Name = $Name; Dest = $Dest })
}

# ---------------------------------------------------------------- 目录
$dirRules = @(
    @{ Match = '^Sunshine-\d{4}\.\d+-source$'; Dest = 'sunshine/sources' }
    @{ Match = '^(Sunshine-portable-|sunshine-v\d{4}\.\d+-portable$|sunshine-v\d{4}\.\d+-extracted$)'; Dest = 'sunshine/packages' }
    @{ Match = '^(Sunshine-prerotation-test|Sunshine-v2025-nvenc-test)$'; Dest = 'sunshine/builds' }
    @{ Match = '^(backup-\d+|sunshine-canonical-backup-.*|rollback-prerotation-.*|huorong-db-snapshot-.*)$'; Dest = 'archive/backups' }
    @{ Match = '^deskflow-\d+\.\d+\.\d+-source$'; Dest = 'deskflow-touch/source' }
    @{ Match = '^(deskflow-filter-keyboard-sandbox|deskflow-private-selftest|deskflow-local-keyboard-runtime.*|longpress-probe)$'; Dest = 'deskflow-touch' }
    @{ Match = '^(android-platform-tools|moonlight-android-official-.*|moonlight-prerotation-apk)$'; Dest = 'moonlight-client' }
    @{ Match = '^__pycache__$'; Dest = 'archive/misc' }
)

# ---------------------------------------------------------------- 文件
$fileRules = @(
    # 显示 / 拓扑 / 网络 脚本（互相通过 $PSScriptRoot 引用，必须同目录）
    @{ Match = '^(set-display-topology|set-primary-display|set-virtual-display-refresh|second-screen-only-low-latency|extend-display-low-latency|clone-display-parsec-primary|restart-sunshine-user-task|parsec-tablet-display-controller|toggle-usb-network|set-moonlight-stream-fps|set-moonlight-stream-resolution|configure-sunshine-usb-network|audit-rndis-low-latency|legacy-desktop-toggle-usb-network|pair-sunshine-stdin)\.ps1$'; Dest = 'scripts' }
    @{ Match = '^launch-clone-display\.vbs$'; Dest = 'scripts' }
    @{ Match = '^update-moonlight-host\.py$'; Dest = 'scripts' }
    @{ Match = '^(display_topology_probe|display_path_probe)\.exe$'; Dest = 'scripts' }
    # 延迟测量
    @{ Match = '^(measure-surfaceflinger-latency\.ps1|measure_stream_latency\.py|inspect_latency_baseline\.py|inspect-moonlight-db\.py|inspect_moonlight_hosts\.py)$'; Dest = 'tools/latency' }
    # Sunshine 脚本与配置
    @{ Match = '^(enable-sunshine-service|restart-sunshine-service|stop-sunshine-service|switch-sunshine-service|repair-sunshine-service-path|set-sunshine-minimum-fps|set-active-sunshine-minimum-fps|set-sunshine-nvenc-low-latency|restore-sunshine-identity|apply-final-sunshine-config|install-sunshine-display-startup-guard|start-sunshine-after-parsec|disable-obsolete-sunshine-guard|diagnose-system-automation|restart-sunshine-latency-ab-elevated|invoke-sunshine-latency-ab|sunshine-prerotation-ab|invoke-sunshine-prerotation-elevated|install-sunshine-prerotation-service|invoke-install-sunshine-prerotation-elevated|consolidate-sunshine|consolidate-sunshine-main|invoke-consolidate-sunshine-elevated|deploy-sunshine-wechat|invoke-deploy-sunshine-touch-elevated|deploy-sunshine-compatible|install-sunshine-v2025\.924|install-nvidia-driver|recover-nvidia-566|admin-context-probe)\.ps1$'; Dest = 'scripts/sunshine' }
    @{ Match = '^(sunshine-unified\.conf|sunshine-min90\.conf|sunshine-before-min90\.conf|sunshine\.conf\.optimized|sunshine-min90\.json|sunshine-min120-rollback\.json|sunshine-min90-save-response\.json)$'; Dest = 'scripts/sunshine' }
    # Deskflow / 触摸
    @{ Match = '^(build-deskflow-touch-filter|build-touch-calibration-overlay|build-touch-mouse-isolator|deploy-deskflow-native-touch-filter|invoke-deploy-deskflow-native-touch-filter-elevated|deploy-independent-touch|restore-deskflow-before-native-touch-filter|sync-deskflow-f24-service|invoke-sync-deskflow-f24-elevated|touch-isolation-watchdog|touch-mouse-isolation|test-wechat-touch-origin)\.ps1$'; Dest = 'deskflow-touch' }
    @{ Match = '^(Deskflow\.touch-release\.conf|deskflow-.*\.(conf|ini))$'; Dest = 'deskflow-touch' }
    @{ Match = '^(deskflow_touch_filter|deskflow_touch_launcher|touch_mouse_isolator)\.(generic.*|before-.*|next|native\.next|baseline|independent)\.(dll|exe|obj)$'; Dest = 'deskflow-touch/variants' }
    @{ Match = '^(deskflow_touch_filter|deskflow_touch_launcher|touch_mouse_isolator|touch_calibration_overlay|touch_route_probe|Weixin(\.dense\.next)?)\.obj$'; Dest = 'deskflow-touch/obj' }
    @{ Match = '^(deskflow_touch_filter|deskflow_touch_launcher|touch_mouse_isolator|touch_calibration_overlay|touch_route_probe|deskflow_touch_filter_shared|Weixin(\.dense\.next)?)\.(cpp|h|dll|exe)$'; Dest = 'deskflow-touch' }
    @{ Match = '\.lnk$'; Dest = 'shortcuts' }
    # 探针
    @{ Match = '^(display_topology_probe|display_path_probe|display_refresh_config|desktop_message_probe|desktop_postmessage_probe|desktop_selection_probe|input_cursor_probe|native_touch_bridge_probe|taskbar_accessible_probe|taskbar_message_probe|taskbar_uia_probe|window_at_point_probe|window_print_probe)(\..*)?\.(cpp|exe|obj)$'; Dest = 'tools/probes' }
    @{ Match = '^build-display-refresh\.ps1$'; Dest = 'tools/probes' }
    @{ Match = '^native_touch_bridge_probe\.log$'; Dest = 'tools/probes' }
    # Moonlight 客户端数据
    @{ Match = '\.apk$'; Dest = 'moonlight-client' }
    @{ Match = '^(computers4-.*\.db|moonlight-debug-.*\.db|moonlight-prerotation-computers4-readonly\.db|moonlight-debug-preferences\.optimized\.xml|moonlight-runtime-preferences\.xml|android-platform-tools\.zip)$'; Dest = 'moonlight-client' }
    # Sunshine 包
    @{ Match = '^(Sunshine-Windows-.*\.zip|sunshine-v.*-portable\.zip|Sunshine-v.*-installer\.exe)$'; Dest = 'sunshine/packages' }
    # 延迟测试产物
    @{ Match = '^(latency-.*\.(jpg|csv)|sf-latency-.*\.json|dxdiag-latency\.txt|rndis-low-latency-audit\.json|touch-calibration-.*\.(csv|log)|touch-current-.*\.(csv|log)|touch-final-native-.*\.(csv|log)|touch-independent-.*\.(csv|log)|touch-isolation-.*\.(csv|log)|touch-wechat-.*\.(csv|log)|touch-route-probe\.(err\.)?log|desktop-touch-route\.(err\.)?log|touch-calibration-.*\.png)$'; Dest = 'archive/latency-tests' }
    # 截图
    @{ Match = '\.(png|bmp|jpg)$'; Dest = 'archive/screenshots' }
    # UI dump
    @{ Match = '\.xml$'; Dest = 'archive/ui-dumps' }
    # 日志
    @{ Match = '\.(log|txt)$'; Dest = 'archive/logs' }
    # 其他
    @{ Match = '^--help$'; Dest = 'archive/misc' }
)

$keepAtRoot = @('MoonlightHub', 'README.md', 'scripts', 'deskflow-touch', 'tools', 'moonlight-client', 'sunshine', 'archive', 'shortcuts', '.stignore', 'desktop.ini')

foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
    if ($keepAtRoot -contains $entry.Name) { continue }
    $rules = if ($entry.PSIsContainer) { $dirRules } else { $fileRules }
    $dest = $null
    foreach ($rule in $rules) {
        if ($entry.Name -match $rule.Match) { $dest = $rule.Dest; break }
    }
    if ($null -eq $dest) { $dest = 'archive/misc' }
    Plan -Name $entry.Name -Dest $dest
}

Write-Output ("计划移动 {0} 项：" -f $moves.Count)
$moves | Group-Object Dest | Sort-Object Name | ForEach-Object { Write-Output ("  {0,-26} {1,4} 项" -f $_.Name, $_.Count) }

if ($DryRun) {
    Write-Output ''
    Write-Output '--- DryRun：逐项计划 ---'
    $moves | Sort-Object Dest, Name | ForEach-Object { Write-Output ("  {0,-26} <- {1}" -f $_.Dest, $_.Name) }
    return
}

foreach ($dir in $layout.Keys) {
    New-Item -ItemType Directory -Path (Join-Path $Root $dir) -Force | Out-Null
}

$manifestPath = Join-Path $Root 'archive/organize-manifest.txt'
$manifest = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $manifestPath) {
    $manifest.AddRange([string[]](Get-Content -LiteralPath $manifestPath -Encoding UTF8))
}
$manifest.Add(('# Moonlight 文件夹整理清单 {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
$manifest.Add('# 格式: 原路径 -> 新路径 （均相对于文件夹根目录）')
$failed = 0
foreach ($m in $moves) {
    $src = Join-Path $Root $m.Name
    $destDir = Join-Path $Root $m.Dest
    $target = Join-Path $destDir $m.Name
    if (Test-Path -LiteralPath $target) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $target = Join-Path $destDir ('{0}.{1}' -f @($m.Name, $stamp))
    }
    try {
        Move-Item -LiteralPath $src -Destination $target -Force
        $rel = $target.Substring($Root.Length).TrimStart('\', '/')
        $manifest.Add(('{0} -> {1}' -f @($m.Name, $rel)))
    } catch {
        $failed++
        $manifest.Add(('# 失败: {0} ({1})' -f @($m.Name, $_.Exception.Message)))
        Write-Warning ('移动失败: {0}: {1}' -f @($m.Name, $_.Exception.Message))
    }
}
foreach ($dir in $layout.Keys) {
    $readme = Join-Path (Join-Path $Root $dir) '_说明.txt'
    if (-not (Test-Path -LiteralPath $readme)) {
        Set-Content -LiteralPath $readme -Encoding UTF8 -Value $layout[$dir]
    }
}
Set-Content -LiteralPath $manifestPath -Encoding UTF8 -Value $manifest
Write-Output ('已完成：移动 {0} 项，失败 {1} 项。清单: {2}' -f @(($moves.Count - $failed), $failed, $manifestPath))
