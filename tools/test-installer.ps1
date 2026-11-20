<#
.SYNOPSIS
  End-to-end test of the MSI on THIS machine, per-user and silent: install, upgrade (settings must survive), uninstall (nothing may be left).

.DESCRIPTION
  Installs -Previous (an older released MSI), then upgrades to -Current and checks the version changed, only one product is
  registered and the settings folder survived; then uninstalls and checks that the program folder (including a file the installer
  did not create), shortcuts, PATH entry, the HKCU Software\Apodofos key, the uninstall entry and the settings folder %LOCALAPPDATA%\Apodofos
  are all gone. Optionally -CurrentTwin (another build of the same version, which has a different product code) is installed over -Current
  first to test same-version upgrades.

  WARNING: a clean uninstall deletes %LOCALAPPDATA%\Apodofos (settings, recent files, WebView2 profile). The script refuses to run when
  that folder already exists, unless you pass -AllowDataLoss. Close Apodofos first (a running copy keeps files in that folder locked).
  Nothing here needs administrator rights and nothing touches the network.

.EXAMPLE
  .\tools\test-installer.ps1 -Previous C:\dl\Apodofos-Setup-1.0.6-win-x64.msi -Current .\artifacts\release\Apodofos-Setup-1.0.7-win-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Previous,
    [Parameter(Mandatory)][string]$Current,
    [string]$CurrentTwin,
    [switch]$AllowDataLoss
)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'Apodofos'
$inst = Join-Path $env:LOCALAPPDATA 'Programs\Apodofos'
$log = Join-Path ([IO.Path]::GetTempPath()) 'apodofos-installer-test'
New-Item -ItemType Directory -Force $log | Out-Null
if ((Test-Path $data) -and -not $AllowDataLoss) { throw "$data exists and the uninstall test would delete it. Close Apodofos, back it up and run with -AllowDataLoss." }
if (Get-Process Apodofos -ErrorAction SilentlyContinue) { Write-Warning 'Apodofos is running: its files in the settings folder stay locked and the cleanup check may fail.' }

$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:fail++ } }
function Msi([string]$verb, [string]$msi, [string]$name, [string]$extra = '') {
    $a = "/$verb `"$msi`" /qn /l*v `"$log\$name.log`" MSIINSTALLPERUSER=1 ALLUSERS=2 $extra"
    $p = Start-Process msiexec -ArgumentList $a -Wait -PassThru
    Check "msiexec /$verb $name exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
}
function Versions { @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -match 'Apodofos') }
function ExeVersion { $e = Join-Path $inst 'Apodofos.exe'; if (Test-Path $e) { (Get-Item $e).VersionInfo.ProductVersion.Split('+')[0] } else { 'missing' } }
$features = 'ADDLOCAL=Main,DesktopShortcutFeature,PathFeature'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Apodofos Report Analyzer.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Apodofos Report Analyzer.lnk'

Write-Host "== install previous"
Check 'nothing installed before the test' ((Versions).Count -eq 0)
Msi 'i' $Previous 'previous' $features
$before = ExeVersion
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'window.json') '{"test":"settings that must survive an upgrade"}'
Check "previous version installed ($before)" ($before -ne 'missing')

Write-Host "== upgrade previous -> current"
Msi 'i' $Current 'current' $features
Check "exe version changed ($before -> $(ExeVersion))" ((ExeVersion) -ne $before)
Check 'exactly one Apodofos registered' ((Versions).Count -eq 1)
Check 'settings survived the upgrade' (Test-Path (Join-Path $data 'window.json'))
if ($CurrentTwin) {
    Write-Host "== same-version upgrade current -> twin"
    Msi 'i' $CurrentTwin 'twin' $features
    Check 'exactly one Apodofos registered after the twin upgrade' ((Versions).Count -eq 1)
    Check 'settings survived the twin upgrade' (Test-Path (Join-Path $data 'window.json'))
}

Write-Host "== uninstall"
Set-Content (Join-Path $inst 'file-the-installer-did-not-create.log') 'x'
Msi 'x' ($(if ($CurrentTwin) { $CurrentTwin } else { $Current })) 'uninstall'
Check 'program folder gone (including a stray file)' (-not (Test-Path $inst))
Check 'Start menu shortcut gone' (-not (Test-Path $startMenu))
Check 'desktop shortcut gone' (-not (Test-Path $desktop))
Check 'PATH entry gone' (([Environment]::GetEnvironmentVariable('Path', 'User')) -notmatch 'Programs\\Apodofos\\cli')
Check 'HKCU\Software\Apodofos gone' (-not (Test-Path 'HKCU:\Software\Apodofos'))
Check 'uninstall entry gone' ((Versions).Count -eq 0)
Check 'settings folder %LOCALAPPDATA%\Apodofos gone' (-not (Test-Path $data))
Write-Host "logs: $log"
if ($fail) { Write-Host "$fail check(s) failed." -ForegroundColor Red; exit 1 } else { Write-Host 'All checks passed.' -ForegroundColor Green }
