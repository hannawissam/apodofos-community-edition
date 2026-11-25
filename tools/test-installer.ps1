<#
.SYNOPSIS
  End-to-end test of the MSI on THIS machine, per-user and silent: install, upgrade (settings must survive), uninstall (nothing of this
  product may be left, nothing of any other product may be touched).

.DESCRIPTION
  Names come from Edition.props (product name, slug, executable names, release prefix). Layout under test:
    install folder   %LOCALAPPDATA%\Programs\Apodofos\<slug>
    data folder      %LOCALAPPDATA%\Apodofos\<slug>
    registry         HKCU\Software\Apodofos\<slug>

  Installs -Previous (an older released MSI: either an OLD flat-layout 1.0.x MSI, installed in Programs\Apodofos, or a 1.1.0 MSI, installed in
  Programs\Apodofos\ReportAnalyzer; the layout is detected after the install), then upgrades to -Current and checks that the version changed,
  only one product is registered, the old install is gone, the new exe and both former command-name shims are installed and all settings
  survived untouched (the OLD flat 1.0.x data in %LOCALAPPDATA%\Apodofos: window.json, recent.json, EBWebView, and the 1.1.0 data folder
  %LOCALAPPDATA%\Apodofos\ReportAnalyzer, which the application moves on its first start). Then it creates unrelated sibling products
  (%LOCALAPPDATA%\Apodofos\OtherTool and PerformanceAnalyzerPro with marker files, and their HKCU\Software\Apodofos keys) and uninstalls,
  checking that this product's install folder (including a file the installer did not create), shortcuts, PATH entry,
  HKCU\Software\Apodofos\<slug> key, uninstall entry, data folder and (Community) the legacy flat items and the 1.1.0 data folder are gone,
  while the sibling folders and keys SURVIVE.
  Optionally -CurrentTwin (another build of the same version, which has a different product code) is installed over -Current first to test
  same-version upgrades.

  WARNING: a clean uninstall deletes the product's data folder (settings, recent files, WebView2 profile) and the three legacy flat items.
  The script refuses to run when any of the paths it will create or the uninstall will delete already exists, unless you pass
  -AllowDataLoss (it then OVERWRITES/DELETES them: back them up first). Close the application first (a running copy keeps files locked).
  Nothing here needs administrator rights and nothing touches the network.

.EXAMPLE
  .\tools\test-installer.ps1 -Previous C:\dl\Apodofos-Setup-1.0.7-win-x64.msi -Current .\artifacts\release\Apodofos-PerformanceReportAnalyzer-Setup-1.2.0-win-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Previous,
    [Parameter(Mandatory)][string]$Current,
    [string]$CurrentTwin,
    [switch]$AllowDataLoss
)
$ErrorActionPreference = 'Stop'

# --- identity from Edition.props ---
[xml]$edition = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'Edition.props')
$ed = @{}
foreach ($n in $edition.Project.PropertyGroup.ChildNodes) { if ($n.NodeType -eq 'Element') { $ed[$n.Name] = $n.InnerText.Trim() } }
$slug = $ed.ApodofosProductSlug
$productName = $ed.ApodofosProductName
$desktopExe = "$($ed.ApodofosDesktopExe).exe"
$legacy = ($ed.ApodofosMigrateLegacyData -eq 'true')
# facts about the earlier generations (fixed history, not identity): 1.0.x = flat layout, 1.1.0 = slug ReportAnalyzer
$slug110 = 'ReportAnalyzer'; $exe110 = 'ApodofosReportAnalyzer.exe'; $name110 = 'Apodofos Report Analyzer'
$shims = @('apodofos.cmd', 'apodofos-report.cmd')

$root = Join-Path $env:LOCALAPPDATA 'Apodofos'                      # shared parent of all products' data
$data = Join-Path $root $slug
$programs = Join-Path $env:LOCALAPPDATA 'Programs\Apodofos'         # shared parent of all products' install folders
$inst = Join-Path $programs $slug
$regRoot = 'HKCU:\Software\Apodofos'
$regSlug = Join-Path $regRoot $slug
$data110 = Join-Path $root $slug110                              # 1.1.0 data folder
$inst110 = Join-Path $programs $slug110                            # 1.1.0 install folder
$reg110 = Join-Path $regRoot $slug110
$other = Join-Path $root 'OtherTool'                               # unrelated product that must survive
$otherMarker = Join-Path $other 'marker.txt'
$otherReg = Join-Path $regRoot 'OtherTool'
$otherPro = Join-Path $root 'PerformanceAnalyzerPro'               # the Professional edition: its own folders, must survive
$otherProMarker = Join-Path $otherPro 'marker.txt'
$otherProReg = Join-Path $regRoot 'PerformanceAnalyzerPro'
$legacyFiles = @((Join-Path $root 'window.json'), (Join-Path $root 'recent.json'))
$legacyDir = Join-Path $root 'EBWebView'
$log = Join-Path ([IO.Path]::GetTempPath()) 'apodofos-installer-test'
New-Item -ItemType Directory -Force $log | Out-Null

$guarded = @($data, $other, $otherReg, $otherPro, $otherProReg, $regSlug, $inst, $inst110, $data110, $reg110) + $(if ($legacy) { $legacyFiles + $legacyDir } else { @() })
$present = @($guarded | Where-Object { Test-Path $_ })
if ($present.Count -and -not $AllowDataLoss) {
    throw "These paths already exist and the test would overwrite or delete them:`n  $($present -join "`n  ")`nClose the application, back them up and run with -AllowDataLoss."
}
foreach ($pn in $ed.ApodofosDesktopExe, 'ApodofosReportAnalyzer', 'Apodofos') {
    if ($ed.ApodofosDesktopExe -and (Get-Process $pn -ErrorAction SilentlyContinue)) { Write-Warning "$pn is running: its files stay locked and the cleanup checks may fail." }
}

$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:fail++ } }
function Msi([string]$verb, [string]$msi, [string]$name, [string]$extra = '') {
    $a = "/$verb `"$msi`" /qn /l*v `"$log\$name.log`" MSIINSTALLPERUSER=1 ALLUSERS=2 $extra"
    $p = Start-Process msiexec -ArgumentList $a -Wait -PassThru
    Check "msiexec /$verb $name exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
}
function Versions { @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -match 'Apodofos') }
function ExeVersion {
    # new layout first, then 1.1.0 (Programs\Apodofos\ReportAnalyzer), then the old flat 1.0.x layout (Programs\Apodofos\Apodofos.exe)
    foreach ($e in (Join-Path $inst $desktopExe), (Join-Path $inst110 $exe110), (Join-Path $programs 'Apodofos.exe')) {
        if (Test-Path $e) { return (Get-Item $e).VersionInfo.ProductVersion.Split('+')[0] }
    }
    'missing'
}
$features = 'ADDLOCAL=Main,DesktopShortcutFeature,PathFeature'
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$productName.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$productName.lnk"
$oldStartMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name110.lnk"          # 1.1.0 shortcut name
$oldDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name110.lnk"

Write-Host "== install previous"
Check 'nothing installed before the test' ((Versions).Count -eq 0)
Msi 'i' $Previous 'previous' $features
$before = ExeVersion
Check "previous version installed ($before)" ($before -ne 'missing')
$oldLayout = Test-Path (Join-Path $programs 'Apodofos.exe')                  # 1.0.x
$prev110 = Test-Path (Join-Path $inst110 $exe110)                                  # 1.1.0
Check 'previous MSI is a 1.0.x (flat) or a 1.1.0 (ReportAnalyzer folder) layout' ($oldLayout -or $prev110)
Write-Host "  previous MSI uses the $(if ($oldLayout) { 'OLD flat 1.0.x' } elseif ($prev110) { '1.1.0 ReportAnalyzer' } else { 'unknown' }) layout"

# settings that must survive the upgrade: the product's own data, plus the old flat data (if the old app wrote it)
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'window.json') '{"test":"settings that must survive an upgrade"}'
New-Item -ItemType Directory -Force $root | Out-Null
$flatText = '{"test":"old flat 1.0.x data that must survive an upgrade untouched"}'
Set-Content (Join-Path $root 'window.json') $flatText
Set-Content (Join-Path $root 'recent.json') $flatText
New-Item -ItemType Directory -Force (Join-Path $legacyDir 'Default') | Out-Null
Set-Content (Join-Path $legacyDir 'Default\profile.txt') 'x'
$text110 = '{"test":"1.1.0 data that must survive an upgrade untouched"}'
New-Item -ItemType Directory -Force (Join-Path $data110 'EBWebView') | Out-Null
Set-Content (Join-Path $data110 'window.json') $text110
Set-Content (Join-Path $data110 'EBWebView\profile.txt') 'x'

Write-Host "== upgrade previous -> current"
Msi 'i' $Current 'current' $features
Check "exe version changed ($before -> $(ExeVersion))" ((ExeVersion) -ne $before)
Check 'new exe is in the per-product install folder' (Test-Path (Join-Path $inst $desktopExe))
Check 'CLI is cli\<CliExe> next to the desktop program' (Test-Path (Join-Path $inst "cli\$($ed.ApodofosCliExe).exe"))
if ($legacy) { foreach ($sh in $shims) { Check "former command name shim cli\$sh present" (Test-Path (Join-Path $inst "cli\$sh")) } }
Check 'exactly one product registered' ((Versions).Count -eq 1)
if ($prev110) {
    Check '1.1.0 install folder gone (Programs\Apodofos\ReportAnalyzer)' (-not (Test-Path $inst110))
    Check '1.1.0 registry key gone (HKCU\Software\Apodofos\ReportAnalyzer)' (-not (Test-Path $reg110))
    Check '1.1.0 Start menu shortcut gone' (-not (Test-Path $oldStartMenu))
    Check '1.1.0 desktop shortcut gone' (-not (Test-Path $oldDesktop))
}
if ($oldLayout) {
    Check 'old install folder content gone (Programs\Apodofos\Apodofos.exe, cli)' (-not (Test-Path (Join-Path $programs 'Apodofos.exe')) -and -not (Test-Path (Join-Path $programs 'cli')))
}
Check 'product settings survived the upgrade' (Test-Path (Join-Path $data 'window.json'))
Check '1.1.0 data folder survived the upgrade untouched (the application migrates it on first start)' ((Test-Path (Join-Path $data110 'window.json')) -and ((Get-Content (Join-Path $data110 'window.json') -Raw).Trim() -eq $text110) -and (Test-Path (Join-Path $data110 'EBWebView\profile.txt')))
Check 'OLD flat window.json survived untouched' ((Test-Path $legacyFiles[0]) -and ((Get-Content $legacyFiles[0] -Raw).Trim() -eq $flatText))
Check 'OLD flat recent.json survived untouched' ((Test-Path $legacyFiles[1]) -and ((Get-Content $legacyFiles[1] -Raw).Trim() -eq $flatText))
Check 'OLD flat EBWebView survived untouched' (Test-Path (Join-Path $legacyDir 'Default\profile.txt'))
if ($CurrentTwin) {
    Write-Host "== same-version upgrade current -> twin"
    Msi 'i' $CurrentTwin 'twin' $features
    Check 'exactly one product registered after the twin upgrade' ((Versions).Count -eq 1)
    Check 'settings survived the twin upgrade' (Test-Path (Join-Path $data 'window.json'))
}

# an unrelated sibling product, created only now (an old-layout uninstall during the upgrade removes the whole old HKCU\Software\Apodofos key)
Write-Host "== create unrelated sibling product (must survive the uninstall)"
New-Item -ItemType Directory -Force $other | Out-Null
Set-Content $otherMarker 'belongs to another product'
New-Item -Path $otherReg -Force | Out-Null
Set-ItemProperty -Path $otherReg -Name 'Marker' -Value 'other'
New-Item -ItemType Directory -Force $otherPro | Out-Null
Set-Content $otherProMarker 'belongs to the Professional edition'
New-Item -Path $otherProReg -Force | Out-Null
Set-ItemProperty -Path $otherProReg -Name 'Marker' -Value 'pro'

Write-Host "== uninstall"
Set-Content (Join-Path $inst 'file-the-installer-did-not-create.log') 'x'
Msi 'x' ($(if ($CurrentTwin) { $CurrentTwin } else { $Current })) 'uninstall'
Check 'program folder gone (including a stray file)' (-not (Test-Path $inst))
Check 'Start menu shortcut gone' (-not (Test-Path $startMenu))
Check 'desktop shortcut gone' (-not (Test-Path $desktop))
Check 'PATH entry gone' (([Environment]::GetEnvironmentVariable('Path', 'User')) -notmatch 'Programs\\Apodofos\\')
Check "HKCU\Software\Apodofos\$slug gone" (-not (Test-Path $regSlug))
Check 'uninstall entry gone' ((Versions).Count -eq 0)
Check "data folder %LOCALAPPDATA%\Apodofos\$slug gone" (-not (Test-Path $data))
if ($legacy) {
    Check 'legacy flat window.json gone' (-not (Test-Path $legacyFiles[0]))
    Check 'legacy flat recent.json gone' (-not (Test-Path $legacyFiles[1]))
    Check 'legacy flat EBWebView gone' (-not (Test-Path $legacyDir))
    Check '1.1.0 data folder %LOCALAPPDATA%\Apodofos\ReportAnalyzer gone' (-not (Test-Path $data110))
}
Write-Host "== sibling product must be untouched"
Check 'sibling data folder and marker survived' ((Test-Path $otherMarker) -and ((Get-Content $otherMarker -Raw).Trim() -eq 'belongs to another product'))
Check 'sibling registry key survived' ((Test-Path $otherReg) -and ((Get-ItemProperty $otherReg).Marker -eq 'other'))
Check 'Professional-edition folder PerformanceAnalyzerPro and marker survived' ((Test-Path $otherProMarker) -and ((Get-Content $otherProMarker -Raw).Trim() -eq 'belongs to the Professional edition'))
Check 'Professional-edition registry key survived' ((Test-Path $otherProReg) -and ((Get-ItemProperty $otherProReg).Marker -eq 'pro'))
Check '%LOCALAPPDATA%\Apodofos root survived (not empty)' (Test-Path $root)

# remove what this script created for the sibling, then tidy the shared parents only if empty
Remove-Item -Recurse -Force $other -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $otherReg -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $otherPro -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $otherProReg -ErrorAction SilentlyContinue
foreach ($d in $root, $programs) { if ((Test-Path $d) -and -not (Get-ChildItem $d -Force)) { Remove-Item $d } }
if ((Test-Path $regRoot) -and -not (Get-ChildItem $regRoot) -and -not (Get-Item $regRoot).ValueCount) { Remove-Item $regRoot }
Write-Host "logs: $log"
if ($fail) { Write-Host "$fail check(s) failed." -ForegroundColor Red; exit 1 } else { Write-Host 'All checks passed.' -ForegroundColor Green }
