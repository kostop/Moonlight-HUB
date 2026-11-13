[CmdletBinding()]
param(
    # Target：Hub 发布目录；只在其中写入 deskflow 与 tools 子目录。
    [Parameter(Mandatory = $true)][string]$Target,
    # DeskflowSource：本仓库构建生成的完整随附组件目录。
    [string]$DeskflowSource = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\deskflow-runtime')
)
$ErrorActionPreference = 'Stop'
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
$DeskflowSource = [IO.Path]::GetFullPath($DeskflowSource).TrimEnd('\')
$destination = Join-Path $Target 'deskflow'
if ($Target -eq [IO.Path]::GetPathRoot($Target).TrimEnd('\')) {
    throw 'Publish Deskflow into a dedicated Hub application directory.'
}
if ($destination.Equals($DeskflowSource, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($DeskflowSource + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $DeskflowSource.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Bundled destination must not overlap the component source.'
}
$files = @(
    'deskflow-core.exe', 'deskflow_touch_filter.dll', 'moonlight-touch-keyboard-release.exe',
    'moonlight-touch-bridge.exe', 'runtime-manifest.json', 'LICENSE',
    'native\deskflow-core.exe', 'native\Qt6Core.dll', 'native\Qt6Gui.dll',
    'native\Qt6Network.dll', 'native\Qt6Svg.dll', 'native\Qt6Widgets.dll',
    'native\libcrypto-3-x64.dll', 'native\libssl-3-x64.dll'
)
foreach ($relative in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $DeskflowSource $relative) -PathType Leaf)) {
        throw "Hub Deskflow component missing: $relative. Run deskflow-patches\build.ps1 first."
    }
}
$copyFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $files) { [void]$copyFiles.Add($relative) }
Get-ChildItem -LiteralPath $DeskflowSource -Directory | Where-Object {$_.Name -ieq 'LICENSES'} | ForEach-Object {
    Get-ChildItem -LiteralPath $_.FullName -File -Recurse | ForEach-Object {
        [void]$copyFiles.Add($_.FullName.Substring($DeskflowSource.Length).TrimStart('\'))
    }
}
# MSVC 的再分发 DLL 与核心放在一起，避免依赖单独安装的 Deskflow。
Get-ChildItem -LiteralPath (Join-Path $DeskflowSource 'native') -Filter '*.dll' -File |
    Where-Object {$_.Name -match '^(msvcp|vcruntime|concrt|vccorlib)'} | ForEach-Object {
        [void]$copyFiles.Add('native\' + $_.Name)
    }
$manifest = Get-Content -LiteralPath (Join-Path $DeskflowSource 'runtime-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.component -ne 'Moonlight Hub bundled Deskflow' -or !$manifest.files) {
    throw 'Invalid Deskflow component manifest; rebuild the component with deskflow-patches\build.ps1.'
}
foreach ($entry in $manifest.files.PSObject.Properties) {
    if (!$copyFiles.Contains($entry.Name)) {
        throw "Deskflow manifest lists a file outside the published component: $($entry.Name)"
    }
}
foreach ($relative in $copyFiles) {
    if ($relative -eq 'runtime-manifest.json') { continue }
    $expected = $manifest.files.PSObject.Properties[$relative].Value
    if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath (Join-Path $DeskflowSource $relative)).Hash -ne $expected) {
        throw "Deskflow component hash mismatch or missing hash: $relative"
    }
}
if ($manifest.coreSha256 -ne $manifest.files.'native\deskflow-core.exe') {
    throw 'Deskflow core hash disagrees with the component manifest.'
}
# 先校验全部输入，再清理明确属于发布目录的生成组件，避免携带旧配置或绝对路径文件。
if (!$destination.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($destination) -ne 'deskflow') { throw 'Unsafe Deskflow publish directory.' }
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
foreach ($relative in $copyFiles) {
    $targetFile = Join-Path $destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetFile) | Out-Null
    Copy-Item -LiteralPath (Join-Path $DeskflowSource $relative) -Destination $targetFile -Force
    if ((Get-FileHash -LiteralPath $targetFile).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $DeskflowSource $relative)).Hash) {
        throw "Copied Deskflow component hash mismatch: $relative"
    }
}
$tools = Join-Path $Target 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item -LiteralPath (Join-Path $DeskflowSource 'moonlight-touch-bridge.exe') -Destination $tools -Force
Write-Output "Bundled Hub Deskflow component: $destination"
