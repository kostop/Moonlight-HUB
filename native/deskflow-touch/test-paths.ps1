[CmdletBinding()]
param(
    # BuildDir：build.ps1 输出的目录；只复制组件，不运行核心或安装钩子。
    [Parameter(Mandatory = $true)][string]$BuildDir
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('moonlight-deskflow-paths-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $fixtureRoot 'relocated bundle'
$native = Join-Path $bundle 'native'
New-Item -ItemType Directory -Path $native -Force | Out-Null
foreach ($name in @('deskflow-core.exe', 'deskflow_touch_filter.dll')) {
    Copy-Item -LiteralPath (Join-Path $BuildDir $name) -Destination (Join-Path $bundle $name)
}
$core = Join-Path $native 'deskflow-core.exe'
# 路径查询不会执行此占位文件；即使当前目录错误也只能检查组件路径。
[IO.File]::WriteAllBytes($core, [byte[]]@(0))
$wrapper = Join-Path $bundle 'deskflow-core.exe'
$checks = 0
Push-Location ([IO.Path]::GetTempPath())
try {
    $paths = @(& $wrapper --touch-filter-paths)
    if ($LASTEXITCODE -ne 0 -or $paths -notcontains ('core=' + $core)) {
        throw 'Moved bundle did not resolve its own native core.'
    }
    ++$checks
    [IO.File]::WriteAllText((Join-Path $bundle 'deskflow-core.real-path.txt'), 'native/deskflow-core.exe', [Text.UTF8Encoding]::new($false))
    $paths = @(& $wrapper --touch-filter-paths)
    if ($LASTEXITCODE -ne 0 -or $paths -notcontains ('core=' + $core)) {
        throw 'Relative core override depended on the process current directory.'
    }
    ++$checks
    Remove-Item -LiteralPath $core
    & $wrapper --touch-filter-paths | Out-Null
    if ($LASTEXITCODE -ne 2) { throw 'An incomplete bundle was not rejected.' }
    ++$checks
} finally { Pop-Location }
Write-Output "PASS: $checks relocated wrapper path checks; no hooks or desktop input."
exit 0
