[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'vdd_keepalive.c'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\src\tools\moonlight-vdd-keepalive.dll')
)

# Builds the native Parsec VDD keep-alive (static CRT, no runtime dependency) with the Visual Studio 2022 tools,
# the same way moonlight\deskflow\touch\build-touch-mouse-isolator.ps1 does.
$ErrorActionPreference = 'Stop'

$visualStudioRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$msvcToolsRoot = Join-Path $visualStudioRoot 'VC\Tools\MSVC'
$msvcVersion = Get-ChildItem -LiteralPath $msvcToolsRoot -Directory |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($null -eq $msvcVersion) {
    throw "Visual C++ tools not found: $msvcToolsRoot"
}

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h') } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($null -eq $sdkVersion) {
    throw "Windows SDK not found: $sdkRoot"
}

$compilerDirectory = Join-Path $msvcVersion.FullName 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDirectory 'cl.exe'

$env:Path = "$compilerDirectory;$env:Path"
$env:INCLUDE = @(
    (Join-Path $msvcVersion.FullName 'include')
    (Join-Path $sdkVersion.FullName 'ucrt')
    (Join-Path $sdkVersion.FullName 'shared')
    (Join-Path $sdkVersion.FullName 'um')
) -join ';'
$env:LIB = @(
    (Join-Path $msvcVersion.FullName 'lib\x64')
    (Join-Path $sdkRoot "Lib\$($sdkVersion.Name)\ucrt\x64")
    (Join-Path $sdkRoot "Lib\$($sdkVersion.Name)\um\x64")
) -join ';'

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$objDir = Join-Path $env:TEMP 'moonlight-vdd-keepalive-build'
New-Item -ItemType Directory -Force $objDir | Out-Null

& $compiler /nologo /W4 /WX /O2 /MT /LD /utf-8 /DUNICODE /D_UNICODE $SourcePath "/Fo$objDir\" "/Fe$OutputPath" /link /NOLOGO "/IMPLIB:$objDir\vdd_keepalive.lib" kernel32.lib
if ($LASTEXITCODE -ne 0) {
    throw "build failed with exit code $LASTEXITCODE"
}
Remove-Item -Force -ErrorAction SilentlyContinue ([IO.Path]::ChangeExtension($OutputPath, '.exp')), ([IO.Path]::ChangeExtension($OutputPath, '.lib'))

Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length, LastWriteTime
