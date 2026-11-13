[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'update-from-publish.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Production updater does not parse.' }
foreach ($name in @('Get-BlockingDeskflowCores', 'Wait-InstalledDeskflowRelease')) {
    $method = $ast.Find({param($Node) $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq $name}, $true)
    if (!$method) { throw "Production updater method not found: $name" }
    . ([scriptblock]::Create($method.Extent.Text))
}
$checks = 0
# Success/Message：只检查模拟进程快照，不启动更新、不读取真实进程、不调用输入或显示接口。
function Assert-Update([bool]$Success, [string]$Message) {
    $script:checks++
    if (!$Success) { throw "FAIL: $Message" }
}
$runtime = 'C:\Hub\deskflow'
$snapshots = @(
    [pscustomobject]@{Id=1;Path='C:\Hub\deskflow\native\deskflow-core.exe'},
    [pscustomobject]@{Id=2;Path='c:\hub\Deskflow\deskflow-core.exe'},
    [pscustomobject]@{Id=3;Path='C:\Hub\deskflow-old\native\deskflow-core.exe'},
    [pscustomobject]@{Id=4;Path='C:\Update\staging\deskflow\native\deskflow-core.exe'},
    [pscustomobject]@{Id=5;Path=$null},
    [pscustomobject]@{Id=6;Path='deskflow-core.exe'}
)
$blocked = @(Get-BlockingDeskflowCores -Processes $snapshots -RuntimeDirectory $runtime)
Assert-Update (($blocked.Id -join ',') -eq '1,2,5,6') 'Installed core, wrapper and unknown paths block copying; staging and similarly named outside directories do not'
Assert-Update (@(Get-BlockingDeskflowCores -Processes @() -RuntimeDirectory $runtime).Count -eq 0) 'No core means no file blocker'

$fakeState = @{Reads=0;Sleeps=0;Permanent=$false}
function Read-DeskflowCoreImages {
    $fakeState.Reads++
    if ($fakeState.Permanent -or $fakeState.Reads -eq 1) {
        [pscustomobject]@{Id=7;Path='C:\Hub\deskflow\native\deskflow-core.exe'}
    } else {
        [pscustomobject]@{Id=7;Path='C:\Update\staging\deskflow\native\deskflow-core.exe'}
    }
}
function Start-Sleep { param([int]$Milliseconds) $fakeState.Sleeps++ }
Wait-InstalledDeskflowRelease -InstallDirectory 'C:\Hub' -TimeoutSeconds 1 | Out-Null
Assert-Update ($fakeState.Reads -eq 2 -and $fakeState.Sleeps -eq 1) 'Updater waits until the old core migrates, then returns without stopping it'
$fakeState.Permanent = $true
$errorText = $null
try { Wait-InstalledDeskflowRelease -InstallDirectory 'C:\Hub' -TimeoutSeconds 0 | Out-Null }
catch { $errorText = $_.Exception.Message }
Assert-Update ($errorText -like '*files were not overwritten*timed out*pid=7*') 'Timeout clearly identifies the blocking core and leaves installed files untouched'
Assert-Update ($fakeState.Sleeps -eq 1) 'Expired timeout does not sleep or spin'
$source = [IO.File]::ReadAllText($path)
$stageStart = $source.LastIndexOf('$first = Replace-Hub', [StringComparison]::Ordinal)
$wait = $source.LastIndexOf('Wait-InstalledDeskflowRelease -InstallDirectory', [StringComparison]::Ordinal)
$copy = $source.LastIndexOf('Copy-Tree $PublishDirectory $InstallDirectory', [StringComparison]::Ordinal)
Assert-Update ($stageStart -ge 0 -and $stageStart -lt $wait -and $wait -lt $copy) 'The production updater waits after starting the staging Hub and before copying into the installation'
Assert-Update ($source -notmatch '(?i)Stop-Process|\.Kill\s*\(') 'Updater contains no force-kill operation'
Write-Output "PASS: $checks updater migration checks; no real process or file update executed."
