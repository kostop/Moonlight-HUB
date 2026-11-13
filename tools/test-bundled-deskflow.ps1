[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$hubRoot = Split-Path -Parent $PSScriptRoot
$bundleScript = Join-Path $PSScriptRoot 'bundle-deskflow.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hub-deskflow-package-' + [guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$checks = 0

# Success/Message：只检查临时文件及脚本结果，不启动核心或调用输入、驱动接口。
function Assert-Package([bool]$Success, [string]$Message) {
    $script:checks++
    if (!$Success) { throw "FAIL: $Message" }
}

# Action/Expected：执行预期失败的真实生产脚本，并核对其错误原因。
function Expect-PackageFailure([scriptblock]$Action, [string]$Expected) {
    $caught = $null
    try { & $Action *> $null } catch { $caught = $_.Exception.Message }
    Assert-Package ($caught -like "*$Expected*") "Expected failure '$Expected', got '$caught'"
}

# Path/Text：创建模拟输入文件；文本文件刻意使用 exe/dll 后缀，但从不执行它们。
function Write-Fixture([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

try {
    $source = Join-Path $testRoot 'source'
    $target = Join-Path $testRoot 'publish'
    $relocated = Join-Path $testRoot 'renamed-publish'
    $relativeFiles = @(
        'deskflow-core.exe', 'deskflow_touch_filter.dll', 'moonlight-touch-keyboard-release.exe',
        'moonlight-touch-bridge.exe', 'LICENSE', 'native\deskflow-core.exe',
        'native\Qt6Core.dll', 'native\Qt6Gui.dll', 'native\Qt6Network.dll', 'native\Qt6Svg.dll',
        'native\Qt6Widgets.dll', 'native\libcrypto-3-x64.dll', 'native\libssl-3-x64.dll',
        'native\vcruntime140.dll', 'native\VCCorlib140.dll', 'LICENSES\GPL-2.0-only.txt', 'LICENSES\nested\OpenSSL.txt'
    )
    $hashes = @{}
    foreach ($relative in $relativeFiles) {
        Write-Fixture (Join-Path $source $relative) ('offline fixture ' + $relative)
        $hashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $source $relative)).Hash
    }
    $manifest = @{component='Moonlight Hub bundled Deskflow'; upstream='v1.26.0';
        coreSha256=$hashes['native\deskflow-core.exe']; files=$hashes}
    $manifestPath = Join-Path $source 'runtime-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Fixture (Join-Path $source 'config\private.pem') 'must not be bundled'
    Write-Fixture (Join-Path $source 'deskflow-core.real-path.txt') 'C:\OldStandalone\deskflow.exe'
    Write-Fixture (Join-Path $source 'obj\test.obj') 'must not be bundled'
    Write-Fixture (Join-Path $target 'tools\keep.txt') 'keep unrelated Hub tool'

    & $bundleScript -Target $target -DeskflowSource $source | Out-Null
    foreach ($relative in $relativeFiles) {
        Assert-Package ((Get-FileHash -LiteralPath (Join-Path (Join-Path $target 'deskflow') $relative)).Hash -eq $hashes[$relative]) "Copied bytes match manifest: $relative"
    }
    Assert-Package (!(Test-Path -LiteralPath (Join-Path $target 'deskflow\config'))) 'Standalone configuration is excluded'
    Assert-Package (!(Test-Path -LiteralPath (Join-Path $target 'deskflow\deskflow-core.real-path.txt'))) 'Absolute launcher override is excluded'
    Assert-Package (!(Test-Path -LiteralPath (Join-Path $target 'deskflow\obj'))) 'Build intermediates are excluded'
    Assert-Package (Test-Path -LiteralPath (Join-Path $target 'tools\keep.txt')) 'Other Hub tools survive bundling'
    Assert-Package ((Get-FileHash -LiteralPath (Join-Path $target 'tools\moonlight-touch-bridge.exe')).Hash -eq $hashes['moonlight-touch-bridge.exe']) 'Hub bridge helper matches its manifest'

    Write-Fixture (Join-Path $target 'deskflow\stale.settings.json') 'old configuration'
    & $bundleScript -Target $target -DeskflowSource $source | Out-Null
    Assert-Package (!(Test-Path -LiteralPath (Join-Path $target 'deskflow\stale.settings.json'))) 'Repeated bundling removes stale component files'
    Assert-Package (!(Test-Path -LiteralPath (Join-Path $target 'deskflow\LICENSES\LICENSES'))) 'Repeated license copying never nests LICENSES'
    & $bundleScript -Target $relocated -DeskflowSource $source | Out-Null
    foreach ($relative in $relativeFiles) {
        Assert-Package ((Get-FileHash -LiteralPath (Join-Path (Join-Path $relocated 'deskflow') $relative)).Hash -eq $hashes[$relative]) "Relocated package preserves bytes: $relative"
    }

    $wrapper = Join-Path $source 'deskflow-core.exe'
    $wrapperText = [IO.File]::ReadAllText($wrapper)
    Remove-Item -LiteralPath $wrapper
    Expect-PackageFailure { & $bundleScript -Target $target -DeskflowSource $source } 'component missing: deskflow-core.exe'
    Assert-Package (Test-Path -LiteralPath (Join-Path $target 'deskflow\deskflow-core.exe')) 'Invalid source does not erase the published component'
    Write-Fixture $wrapper $wrapperText
    $dependency = Join-Path $source 'native\Qt6Network.dll'
    $dependencyText = [IO.File]::ReadAllText($dependency)
    Write-Fixture $dependency 'tampered dependency'
    Expect-PackageFailure { & $bundleScript -Target $target -DeskflowSource $source } 'hash mismatch'
    Write-Fixture $dependency $dependencyText
    $manifest.files.Remove('deskflow_touch_filter.dll')
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Expect-PackageFailure { & $bundleScript -Target $target -DeskflowSource $source } 'missing hash'
    $manifest.files['deskflow_touch_filter.dll'] = (Get-FileHash -LiteralPath (Join-Path $source 'deskflow_touch_filter.dll')).Hash
    $manifest.coreSha256 = '0' * 64
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Expect-PackageFailure { & $bundleScript -Target $target -DeskflowSource $source } 'core hash disagrees'
    $manifest.coreSha256 = $hashes['native\deskflow-core.exe']
    $unexpected = 'native\unexpected-runtime.dll'
    Write-Fixture (Join-Path $source $unexpected) 'valid hash but not part of the published component'
    $manifest.files[$unexpected] = (Get-FileHash -LiteralPath (Join-Path $source $unexpected)).Hash
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $publishedBefore = @(Get-ChildItem -LiteralPath (Join-Path $target 'deskflow') -File -Recurse |
        Sort-Object FullName | ForEach-Object {$_.FullName + '|' + (Get-FileHash -LiteralPath $_.FullName).Hash}) -join "`n"
    Expect-PackageFailure { & $bundleScript -Target $target -DeskflowSource $source } 'outside the published component'
    $publishedAfter = @(Get-ChildItem -LiteralPath (Join-Path $target 'deskflow') -File -Recurse |
        Sort-Object FullName | ForEach-Object {$_.FullName + '|' + (Get-FileHash -LiteralPath $_.FullName).Hash}) -join "`n"
    Assert-Package ($publishedAfter -ceq $publishedBefore) 'An unknown manifest entry leaves every published file and hash unchanged'
    Assert-Package (!(Test-Path -LiteralPath (Join-Path (Join-Path $target 'deskflow') $unexpected))) 'Unknown manifest entries cannot add unpublished DLLs'
    Expect-PackageFailure { & $bundleScript -Target $testRoot -DeskflowSource (Join-Path $testRoot 'deskflow') } 'must not overlap'

    # 仅替换 git 边界，在临时 Hub 副本上执行真实 build.ps1 的 clone/apply 控制流。
    # 检查首次 reverse --check 的正常 stderr 不终止 WinPS 5，也检查重复运行不会重复应用补丁。
    $fixtureHub = Join-Path $testRoot 'fixture-hub'
    $fixturePatchDir = Join-Path $fixtureHub 'deskflow-patches'
    New-Item -ItemType Directory -Force -Path $fixturePatchDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $hubRoot 'deskflow-patches\build.ps1') -Destination $fixturePatchDir
    $fixturePatch = "diff --git a/component.txt b/component.txt`n--- a/component.txt`n+++ b/component.txt`n"
    Write-Fixture (Join-Path $fixturePatchDir '01-core.patch') $fixturePatch
    Write-Fixture (Join-Path $fixturePatchDir '02-key.patch') $fixturePatch
    $fakeGitState = @{Clones=0; Notices=0; LfClone=$false; Applies=0; AppliedNames=[Collections.Generic.List[string]]::new(); ForwardValid=$true; Base='760e3b99b00053647a96b405276bf614bd860075'}
    function git {
        $arguments = @($args)
        $global:LASTEXITCODE = 0
        if ($arguments[0] -eq 'clone') {
            $fakeGitState.Clones++
            $fakeGitState.LfClone = $arguments -contains 'core.autocrlf=false'
            $fakeGitState.Notices++
            Write-Error 'Cloning into offline fixture... (normal stderr, exit code 0)'
            New-Item -ItemType Directory -Force -Path $arguments[-1] | Out-Null
            return
        }
        if ($arguments[2] -eq 'rev-parse') { $fakeGitState.Base; return }
        if ($arguments[2] -eq 'read-tree') { $global:LASTEXITCODE = 1; return }
        if ($arguments[2] -ne 'apply') { throw 'Unexpected fixture git command' }
        $patchName = [IO.Path]::GetFileName($arguments[-1])
        $marker = Join-Path $arguments[1] ('.applied-' + $patchName)
        if ($arguments -contains '--reverse') {
            if (!(Test-Path -LiteralPath $marker)) {
                Write-Error 'Expected reverse check failure on pristine source'
                $global:LASTEXITCODE = 1
            }
            return
        }
        if ($arguments -contains '--check') {
            if (!$fakeGitState.ForwardValid) { Write-Error 'Patch cannot be applied'; $global:LASTEXITCODE = 1 }
            return
        }
        $fakeGitState.Applies++
        $fakeGitState.AppliedNames.Add($patchName)
        Write-Fixture $marker 'applied'
        Write-Fixture (Join-Path $arguments[1] 'component.txt') ('source after ' + $patchName)
    }
    $buildScript = Join-Path $fixturePatchDir 'build.ps1'
    & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly | Out-Null
    & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly | Out-Null
    Assert-Package ($fakeGitState.Clones -eq 1) 'Repeated preparation clones only once'
    Assert-Package ($fakeGitState.Notices -eq 1) 'Normal clone stderr does not terminate WinPS preparation'
    Assert-Package $fakeGitState.LfClone 'Pinned clone disables autocrlf so both patches use LF context'
    Assert-Package ($fakeGitState.Applies -eq 2) 'Repeated preparation applies each initial patch only once'
    $receiptPath = Join-Path $fixtureHub 'artifacts\deskflow-upstream\.moonlight-hub-patches.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Package ($receipt.files.'component.txt' -eq (Get-FileHash -LiteralPath (Join-Path $fixtureHub 'artifacts\deskflow-upstream\component.txt')).Hash) 'Preparation receipt records actual patched source bytes'
    Write-Fixture (Join-Path $fixturePatchDir '03-modifier.patch') $fixturePatch
    $componentPath = Join-Path $fixtureHub 'artifacts\deskflow-upstream\component.txt'
    $prefixContent = [IO.File]::ReadAllText($componentPath)
    Write-Fixture $componentPath 'local source edit must survive'
    Expect-PackageFailure { & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly } 'Cached patched source changed'
    Assert-Package ($fakeGitState.Applies -eq 2 -and [IO.File]::ReadAllText($componentPath) -eq 'local source edit must survive') 'A changed prefix source is not overwritten or patched'
    Write-Fixture $componentPath $prefixContent
    Write-Fixture (Join-Path $fixturePatchDir '01-core.patch') ($fixturePatch + '# changed prefix hash')
    Expect-PackageFailure { & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly } 'Cached patch prefix changed'
    Assert-Package ($fakeGitState.Applies -eq 2) 'A changed prior patch hash cannot replay the full patch sequence'
    Write-Fixture (Join-Path $fixturePatchDir '01-core.patch') $fixturePatch
    & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly | Out-Null
    & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly | Out-Null
    Assert-Package (($fakeGitState.AppliedNames -join ',') -eq '01-core.patch,02-key.patch,03-modifier.patch') 'Append upgrade applies only the new suffix and never replays the prior patches'
    Assert-Package ($fakeGitState.Applies -eq 3) 'Repeated preparation after an append upgrade does not reapply the new patch'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Package (@($receipt.patches.PSObject.Properties).Count -eq 3 -and
        $receipt.files.'component.txt' -eq (Get-FileHash -LiteralPath $componentPath).Hash) 'Append upgrade writes the complete new receipt and final source hash'
    $fakeGitState.Base = 'unexpected-commit'
    Expect-PackageFailure { & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly } 'Unexpected upstream revision'
    $fakeGitState.Base = '760e3b99b00053647a96b405276bf614bd860075'
    Get-ChildItem -LiteralPath (Join-Path $fixtureHub 'artifacts\deskflow-upstream') -Filter '.applied-*' -File |
        ForEach-Object {Remove-Item -LiteralPath $_.FullName}
    Remove-Item -LiteralPath $receiptPath
    $fakeGitState.ForwardValid = $false
    Expect-PackageFailure { & $buildScript -QtRoot 'unused' -OpenSslRoot 'unused' -DependencySource 'unused' -PrepareOnly } 'Patch does not apply'

    # 从当前生产脚本解析并执行完整函数定义；只替换文件版本查询，不维护逻辑副本。
    $tokens = $null; $parseErrors = $null
    $buildAst = [Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$parseErrors)
    $resolver = $buildAst.Find({param($Node) $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq 'Resolve-QtRuntimeFile'}, $true)
    if (!$resolver) { throw 'Qt runtime resolver not found in the production build script.' }
    . ([scriptblock]::Create($resolver.Extent.Text))
    $qtBin = Join-Path $testRoot 'qt\bin'
    $qtFallback = Join-Path $testRoot 'dependencies'
    Write-Fixture (Join-Path $qtBin 'Qt6Core.dll') 'fixture Qt core'
    Write-Fixture (Join-Path $qtBin 'Qt6Gui.dll') 'fixture SDK GUI'
    Write-Fixture (Join-Path $qtFallback 'Qt6Gui.dll') 'unused fallback GUI'
    Write-Fixture (Join-Path $qtFallback 'Qt6Svg.dll') 'fixture optional SVG'
    $versions = @{}
    $versions[(Join-Path $qtBin 'Qt6Core.dll')] = '6.10.1.0'
    $versions[(Join-Path $qtBin 'Qt6Gui.dll')] = '6.10.1.0'
    $versions[(Join-Path $qtFallback 'Qt6Gui.dll')] = '6.9.0.0'
    $versions[(Join-Path $qtFallback 'Qt6Svg.dll')] = '6.10.1.0'
    $readVersion = {param($Path) $versions[$Path]}
    $qtCore = Resolve-QtRuntimeFile -Name 'Qt6Core.dll' -QtBin $qtBin -DependencyDir $qtFallback -ReadVersion $readVersion
    Assert-Package ($qtCore.Version -eq '6.10.1.0') 'QtCore establishes the actual PE file version baseline'
    $gui = Resolve-QtRuntimeFile -Name 'Qt6Gui.dll' -QtBin $qtBin -DependencyDir $qtFallback -ExpectedVersion $qtCore.Version -ReadVersion $readVersion
    Assert-Package ($gui.Path -eq (Join-Path $qtBin 'Qt6Gui.dll')) 'Qt SDK DLL wins over a different fallback DLL'
    $svg = Resolve-QtRuntimeFile -Name 'Qt6Svg.dll' -QtBin $qtBin -DependencyDir $qtFallback -ExpectedVersion $qtCore.Version -ReadVersion $readVersion
    Assert-Package ($svg.Path -eq (Join-Path $qtFallback 'Qt6Svg.dll')) 'Missing optional Qt module uses a matching fallback DLL'
    $versions[$svg.Path] = '6.9.0.0'
    Expect-PackageFailure { Resolve-QtRuntimeFile -Name 'Qt6Svg.dll' -QtBin $qtBin -DependencyDir $qtFallback -ExpectedVersion $qtCore.Version -ReadVersion $readVersion } 'version mismatch'
    Expect-PackageFailure { Resolve-QtRuntimeFile -Name 'Qt6Missing.dll' -QtBin $qtBin -DependencyDir $qtFallback -ExpectedVersion $qtCore.Version -ReadVersion $readVersion } 'DLL is missing'

    Write-Output "PASS: $checks bundled Deskflow packaging checks; no core, driver or input operation executed."
} finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (!$testRoot.StartsWith($temporaryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a fixture outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
