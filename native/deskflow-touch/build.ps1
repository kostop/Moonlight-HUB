[CmdletBinding()]
param(
    # OutputDir：仅接收编译产物的目录，不启动或安装组件。
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\..\artifacts\deskflow-touch'),
    # VisualStudioRoot：可选的 Visual Studio 根目录；默认通过 vswhere 查找 C++ 工具。
    [string]$VisualStudioRoot,
    # WindowsSdkRoot：含 Include 和 Lib 子目录的 Windows 10/11 SDK 根目录。
    [string]$WindowsSdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
)
$ErrorActionPreference = 'Stop'
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (!$VisualStudioRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found; pass -VisualStudioRoot.' }
    $VisualStudioRoot = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
}
if (!$VisualStudioRoot) { throw 'Visual Studio C++ x64 tools not found.' }
$msvc = Get-ChildItem -LiteralPath (Join-Path $VisualStudioRoot 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$sdk = Get-ChildItem -LiteralPath (Join-Path $WindowsSdkRoot 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (!$msvc -or !$sdk) { throw 'MSVC or Windows SDK is missing.' }
$compilerDir = Join-Path $msvc.FullName 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDir 'cl.exe'
$env:PATH = "$compilerDir;$env:PATH"
$env:INCLUDE = @((Join-Path $msvc.FullName 'include'), (Join-Path $sdk.FullName 'ucrt'),
    (Join-Path $sdk.FullName 'shared'), (Join-Path $sdk.FullName 'um')) -join ';'
$env:LIB = @((Join-Path $msvc.FullName 'lib\x64'), (Join-Path $WindowsSdkRoot "Lib\$($sdk.Name)\ucrt\x64"),
    (Join-Path $WindowsSdkRoot "Lib\$($sdk.Name)\um\x64")) -join ';'
$objectDir = Join-Path $OutputDir 'obj'
New-Item -ItemType Directory -Force -Path $OutputDir, $objectDir | Out-Null
$common = @('/nologo', '/EHsc', '/std:c++17', '/W4', '/O2', '/MT', '/utf-8', '/DUNICODE', '/D_UNICODE')
$filter = Join-Path $OutputDir 'deskflow_touch_filter.dll'
$wrapper = Join-Path $OutputDir 'deskflow-core.exe'
$keyboard = Join-Path $OutputDir 'moonlight-touch-keyboard-release.exe'

& $compiler @common /LD (Join-Path $PSScriptRoot 'deskflow_touch_filter.cpp') "/Fo$objectDir\deskflow_touch_filter.obj" "/Fe$filter" /link /NOLOGO "/IMPLIB:$objectDir\deskflow_touch_filter.lib" user32.lib
if ($LASTEXITCODE -ne 0) { throw 'Deskflow touch filter build failed.' }
& $compiler @common (Join-Path $PSScriptRoot 'deskflow_touch_launcher.cpp') "/Fo$objectDir\deskflow_touch_launcher.obj" "/Fe$wrapper" /link /NOLOGO user32.lib shell32.lib
if ($LASTEXITCODE -ne 0) { throw 'Deskflow wrapper build failed.' }
& $compiler @common (Join-Path $PSScriptRoot 'touch_mouse_isolator.cpp') "/Fo$objectDir\touch_mouse_isolator.obj" "/Fe$keyboard" /link /NOLOGO user32.lib
if ($LASTEXITCODE -ne 0) { throw 'Deskflow keyboard release helper build failed.' }
# bridge 与 keyboard-release 是同一个实现的不同运行模式，Hub 分别使用对应文件名。
Copy-Item -LiteralPath $keyboard -Destination (Join-Path $OutputDir 'moonlight-touch-bridge.exe') -Force
& $wrapper --touch-filter-self-test
if ($LASTEXITCODE -ne 0) { throw 'Deskflow wrapper self-test failed.' }
& $keyboard self-test
if ($LASTEXITCODE -ne 0) { throw 'Deskflow keyboard release self-test failed.' }
& (Join-Path $PSScriptRoot 'test-paths.ps1') -BuildDir $OutputDir
Get-Item -LiteralPath $filter, $wrapper, $keyboard, (Join-Path $OutputDir 'moonlight-touch-bridge.exe') |
    Select-Object FullName, Length
