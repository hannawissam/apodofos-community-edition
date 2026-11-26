<#
.SYNOPSIS
  End-to-end test of the MSI on THIS machine, per-user and silent: install, upgrade (settings must survive), uninstall (nothing of this
  product may be left, nothing of any other product may be touched).

.DESCRIPTION
  Names come from Edition.props (product name, slug, executable names, release prefix). Layout under test:
    install folder   %LOCALAPPDATA%\Programs\Apodofos\<slug>
    data folder      %LOCALAPPDATA%\Apodofos\<slug>
    registry         HKCU\Software\Apodofos\<slug>

  Installs -Previous (an older released MSI; it may be an OLD flat-layout 1.0.x MSI), then upgrades to -Current and checks that the version
  changed, only one product is registered, the old install folder Programs\Apodofos is gone (old layout) and all settings survived
  (including the OLD flat data in %LOCALAPPDATA%\Apodofos: window.json, recent.json, EBWebView). Then it creates an unrelated sibling
  product (%LOCALAPPDATA%\Apodofos\OtherTool\marker.txt and HKCU\Software\Apodofos\OtherTool) and uninstalls, checking that this product's
  default install folder (including a file the installer did not create), shortcuts, PATH entry, HKCU\Software\Apodofos\<slug> key,
  uninstall entry, data folder and (Community) the three legacy flat items are gone, while the sibling folder and key SURVIVE.
  Optionally -CurrentTwin (another build of the same version, which has a different product code) is installed over -Current first to test
  same-version upgrades.

  Last, it installs into a folder of the user's choice (a new temporary folder holding the script's own marker files), once fresh and once
  as an upgrade from -Previous, and checks that the uninstall removes the product's files only: the marker files must SURVIVE.

  The script stops before doing anything when a product of this edition's upgrade family is already installed, whatever its name or version.

  WARNING: a clean uninstall deletes the product's data folder (settings, recent files, WebView2 profile) and the three legacy flat items.
  The script refuses to run when any of the paths it will create or the uninstall will delete already exists, unless you pass
  -AllowDataLoss (it then OVERWRITES/DELETES them: back them up first). Close the application first (a running copy keeps files locked).
  Nothing here needs administrator rights and nothing touches the network.

.EXAMPLE
  .\tools\test-installer.ps1 -Previous C:\dl\Apodofos-Setup-1.0.6-win-x64.msi -Current .\artifacts\release\Apodofos-ReportAnalyzer-Setup-1.1.0-win-x64.msi
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

$root = Join-Path $env:LOCALAPPDATA 'Apodofos'                      # shared parent of all products' data
$data = Join-Path $root $slug
$programs = Join-Path $env:LOCALAPPDATA 'Programs\Apodofos'         # shared parent of all products' install folders
$inst = Join-Path $programs $slug
$regRoot = 'HKCU:\Software\Apodofos'
$regSlug = Join-Path $regRoot $slug
$other = Join-Path $root 'OtherTool'
$otherMarker = Join-Path $other 'marker.txt'
$otherReg = Join-Path $regRoot 'OtherTool'
$legacyFiles = @((Join-Path $root 'window.json'), (Join-Path $root 'recent.json'))
$legacyDir = Join-Path $root 'EBWebView'
$log = Join-Path ([IO.Path]::GetTempPath()) 'apodofos-installer-test'
New-Item -ItemType Directory -Force $log | Out-Null

# products of this upgrade family, whatever their name: installing a test MSI would replace (or be refused by) any of them
function Versions {
    $i = New-Object -ComObject WindowsInstaller.Installer
    @($i.RelatedProducts('{' + $ed.ApodofosUpgradeCode.ToUpperInvariant() + '}') | Where-Object { $_ })
}
$installed = @(Versions)
if ($installed.Count) {
    throw "A product of this upgrade family is already installed (product code $($installed -join ', ')). The test would replace and then remove it: uninstall it yourself first, or run the test in another Windows account or a virtual machine."
}

$guarded = @($data, $other, $otherReg, $regSlug, $inst) + $(if ($legacy) { $legacyFiles + $legacyDir } else { @() })
$present = @($guarded | Where-Object { Test-Path $_ })
if ($present.Count -and -not $AllowDataLoss) {
    throw "These paths already exist and the test would overwrite or delete them:`n  $($present -join "`n  ")`nClose the application, back them up and run with -AllowDataLoss."
}
foreach ($pn in 'ApodofosReportAnalyzer', 'Apodofos') {
    if ($ed.ApodofosDesktopExe -and (Get-Process $pn -ErrorAction SilentlyContinue)) { Write-Warning "$pn is running: its files stay locked and the cleanup checks may fail." }
}

$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:fail++ } }
function Msi([string]$verb, [string]$msi, [string]$name, [string]$extra = '') {
    $a = "/$verb `"$msi`" /qn /l*v `"$log\$name.log`" MSIINSTALLPERUSER=1 ALLUSERS=2 $extra"
    $p = Start-Process msiexec -ArgumentList $a -Wait -PassThru
    Check "msiexec /$verb $name exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
}
function ExeVersion {
    # new layout first, then the old flat 1.0.x layout (Programs\Apodofos\Apodofos.exe)
    foreach ($e in (Join-Path $inst $desktopExe), (Join-Path $programs 'Apodofos.exe')) {
        if (Test-Path $e) { return (Get-Item $e).VersionInfo.ProductVersion.Split('+')[0] }
    }
    'missing'
}
$features = 'ADDLOCAL=Main,DesktopShortcutFeature,PathFeature'
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$productName.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$productName.lnk"
$oldStartMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Apodofos Report Analyzer.lnk'
$oldDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Apodofos Report Analyzer.lnk'

Write-Host "== install previous"
Msi 'i' $Previous 'previous' $features
$before = ExeVersion
Check "previous version installed ($before)" ($before -ne 'missing')
$oldLayout = Test-Path (Join-Path $programs 'Apodofos.exe')
Write-Host "  previous MSI uses the $(if ($oldLayout) { 'OLD flat' } else { 'per-product' }) layout"

# settings that must survive the upgrade: the product's own data, plus (the edition that claims it) the old flat data
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'window.json') '{"test":"settings that must survive an upgrade"}'
$flatText = '{"test":"old flat 1.0.x data that must survive an upgrade untouched"}'
if ($legacy) {
    Set-Content (Join-Path $root 'window.json') $flatText
    Set-Content (Join-Path $root 'recent.json') $flatText
    New-Item -ItemType Directory -Force (Join-Path $legacyDir 'Default') | Out-Null
    Set-Content (Join-Path $legacyDir 'Default\profile.txt') 'x'
}

Write-Host "== upgrade previous -> current"
Msi 'i' $Current 'current' $features
Check "exe version changed ($before -> $(ExeVersion))" ((ExeVersion) -ne $before)
Check 'new exe is in the per-product install folder' (Test-Path (Join-Path $inst $desktopExe))
Check 'exactly one product registered' ((Versions).Count -eq 1)
if ($oldLayout) {
    Check 'old install folder content gone (Programs\Apodofos\Apodofos.exe, cli)' (-not (Test-Path (Join-Path $programs 'Apodofos.exe')) -and -not (Test-Path (Join-Path $programs 'cli')))
    Check 'old Start menu shortcut gone' (-not (Test-Path $oldStartMenu))
    Check 'old desktop shortcut gone' (-not (Test-Path $oldDesktop))
}
Check 'product settings survived the upgrade' (Test-Path (Join-Path $data 'window.json'))
if ($legacy) {
    Check 'OLD flat window.json survived untouched' ((Test-Path $legacyFiles[0]) -and ((Get-Content $legacyFiles[0] -Raw).Trim() -eq $flatText))
    Check 'OLD flat recent.json survived untouched' ((Test-Path $legacyFiles[1]) -and ((Get-Content $legacyFiles[1] -Raw).Trim() -eq $flatText))
    Check 'OLD flat EBWebView survived untouched' (Test-Path (Join-Path $legacyDir 'Default\profile.txt'))
}
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

Write-Host "== uninstall"
Set-Content (Join-Path $inst 'file-the-installer-did-not-create.log') 'x'
Msi 'x' ($(if ($CurrentTwin) { $CurrentTwin } else { $Current })) 'uninstall'
Check 'program folder gone (including a stray file)' (-not (Test-Path $inst))
Check 'Start menu shortcut gone' (-not (Test-Path $startMenu))
Check 'desktop shortcut gone' (-not (Test-Path $desktop))
# this product's own entry only (and the old flat one for the edition that claims it): other products keep theirs
$pathPattern = "Programs\\Apodofos\\$([regex]::Escape($slug))\\" + $(if ($legacy) { '|Programs\\Apodofos\\cli' })
Check 'PATH entry gone' (([Environment]::GetEnvironmentVariable('Path', 'User')) -notmatch $pathPattern)
Check "HKCU\Software\Apodofos\$slug gone" (-not (Test-Path $regSlug))
Check 'uninstall entry gone' ((Versions).Count -eq 0)
Check "data folder %LOCALAPPDATA%\Apodofos\$slug gone" (-not (Test-Path $data))
if ($legacy) {
    Check 'legacy flat window.json gone' (-not (Test-Path $legacyFiles[0]))
    Check 'legacy flat recent.json gone' (-not (Test-Path $legacyFiles[1]))
    Check 'legacy flat EBWebView gone' (-not (Test-Path $legacyDir))
}

# a folder of the user's choice: always new, holding only this script's marker files (an installer that deletes too much harms nothing else)
$markers = 'user-file.txt', 'user sub\deep.txt', 'cli\user-cli-file.txt'
foreach ($case in 'fresh', 'upgrade') {
    Write-Host "== user-chosen install folder ($case)"
    $chosen = Join-Path $log "chosen-$case-$([guid]::NewGuid().ToString('N'))\my folder"
    New-Item -ItemType Directory -Force (Join-Path $chosen 'user sub'), (Join-Path $chosen 'cli') | Out-Null
    foreach ($m in $markers) { Set-Content (Join-Path $chosen $m) 'belongs to the user' }
    $folder = "INSTALLFOLDER=`"$chosen`""
    if ($case -eq 'upgrade') { Msi 'i' $Previous "chosen-$case-previous" $folder }
    Msi 'i' $Current "chosen-$case-current" $folder
    Check 'installed into the chosen folder' (Test-Path (Join-Path $chosen $desktopExe))
    Check 'exactly one product registered' ((Versions).Count -eq 1)
    Msi 'x' $Current "chosen-$case-uninstall"
    Check 'product executables gone from the chosen folder' (-not (Test-Path (Join-Path $chosen $desktopExe)) -and -not (Test-Path (Join-Path $chosen "cli\$($ed.ApodofosCliExe).exe")))
    Check 'every file of the user survived the uninstall' (@($markers | Where-Object { Test-Path (Join-Path $chosen $_) }).Count -eq $markers.Count)
    Check 'only the files of the user are left' (@(Get-ChildItem $chosen -Recurse -Force -File -ErrorAction SilentlyContinue).Count -eq $markers.Count)
    Check 'uninstall entry gone' ((Versions).Count -eq 0)
}

Write-Host "== sibling product must be untouched"
Check 'sibling data folder and marker survived' ((Test-Path $otherMarker) -and ((Get-Content $otherMarker -Raw).Trim() -eq 'belongs to another product'))
Check 'sibling registry key survived' ((Test-Path $otherReg) -and ((Get-ItemProperty $otherReg).Marker -eq 'other'))
Check '%LOCALAPPDATA%\Apodofos root survived (not empty)' (Test-Path $root)

# remove what this script created for the sibling, then tidy the shared parents only if empty
Remove-Item -Recurse -Force $other -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $otherReg -ErrorAction SilentlyContinue
foreach ($d in $root, $programs) { if ((Test-Path $d) -and -not (Get-ChildItem $d -Force)) { Remove-Item $d } }
if ((Test-Path $regRoot) -and -not (Get-ChildItem $regRoot) -and -not (Get-Item $regRoot).ValueCount) { Remove-Item $regRoot }
Write-Host "logs: $log"
if ($fail) { Write-Host "$fail check(s) failed." -ForegroundColor Red; exit 1 } else { Write-Host 'All checks passed.' -ForegroundColor Green }
