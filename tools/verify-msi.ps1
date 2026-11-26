<#
.SYNOPSIS
  STATIC validator for a built MSI. Opens the database READ-ONLY through the WindowsInstaller COM object; it never installs, repairs,
  uninstalls or runs msiexec, and never touches the real %LOCALAPPDATA%\Apodofos or HKCU\Software\Apodofos.

.DESCRIPTION
  Asserts, against the identity in Edition.props:
    * ProductName and UpgradeCode match; a MajorUpgrade (Upgrade row + RemoveExistingProducts) is present
    * every installed directory sits under %LOCALAPPDATA%\Programs\Apodofos\<slug> (plus the Start menu / desktop shortcut folders)
    * registry rows are under HKCU Software\Apodofos\<slug> only
    * custom actions delete only the product's own folders: the DEFAULT install folder %LOCALAPPDATA%\Programs\Apodofos\<slug> (spelled out;
      never [APPLICATIONFOLDER] or any other folder the user can choose), %LOCALAPPDATA%\Apodofos\<slug>, a NON-recursive rmdir of the
      shared parents, and (only when ApodofosMigrateLegacyData is true) exactly the three 1.0.x flat items window.json, recent.json, EBWebView
    * shortcuts target <ApodofosDesktopExe>.exe
    * files include <ApodofosDesktopExe>.exe, cli\<ApodofosCliExe>.exe and (legacy-data edition) cli\apodofos.cmd; none is named Apodofos.exe/apodofos.exe
  Exit code 0 when everything passes, 1 otherwise.

.EXAMPLE
  .\tools\verify-msi.ps1 -Msi .\artifacts\release\Apodofos-ReportAnalyzer-Setup-1.1.0-win-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [string]$Props = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Edition.props')
)
$ErrorActionPreference = 'Stop'

[xml]$edition = Get-Content $Props
$ed = @{}
foreach ($n in $edition.Project.PropertyGroup.ChildNodes) { if ($n.NodeType -eq 'Element') { $ed[$n.Name] = $n.InnerText.Trim() } }
foreach ($k in 'ApodofosProductName', 'ApodofosProductSlug', 'ApodofosDesktopExe', 'ApodofosCliExe', 'ApodofosUpgradeCode') { if (-not $ed[$k]) { throw "$Props is missing $k" } }
$slug = $ed.ApodofosProductSlug
$desktopExe = "$($ed.ApodofosDesktopExe).exe"
$cliExe = "$($ed.ApodofosCliExe).exe"
$legacy = ($ed.ApodofosMigrateLegacyData -eq 'true')
$upgradeCode = '{' + $ed.ApodofosUpgradeCode.ToUpperInvariant() + '}'

$Msi = (Resolve-Path $Msi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Msi, 0))   # 0 = msiOpenDatabaseModeReadOnly

function Query([string]$sql, [int]$cols) {
    # emits each row as one array object (callers wrap in @())
    try { $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql)) } catch { return }   # table absent
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        $row = @(1..$cols | ForEach-Object { [string]$rec.StringData([int]$_) })
        , $row
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
}
function LongName([string]$n) { if ($n -like '*|*') { $n.Split('|')[1] } else { $n } }

$fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what $detail" -ForegroundColor Red; $script:fail++ }
}

Write-Host "== $Msi"
Write-Host "   identity: $($ed.ApodofosProductName) / slug $slug / $desktopExe / $cliExe / legacy-data $legacy"

# --- properties / upgrade ---
$prop = @{}
foreach ($r in @(Query 'SELECT Property, Value FROM Property' 2)) { $prop[$r[0]] = $r[1] }
Check "ProductName = '$($ed.ApodofosProductName)'" ($prop['ProductName'] -ceq $ed.ApodofosProductName) "(got '$($prop['ProductName'])')"
Check "UpgradeCode = $upgradeCode" ($prop['UpgradeCode'] -eq $upgradeCode) "(got '$($prop['UpgradeCode'])')"
$up = @(Query 'SELECT UpgradeCode, VersionMin, VersionMax, Attributes FROM Upgrade' 4)
Check 'MajorUpgrade: Upgrade row for the UpgradeCode (older versions)' (@($up | Where-Object { $_[0] -eq $upgradeCode -and $_[1] -ne '' }).Count -ge 1)
$seq = @(Query 'SELECT Action FROM InstallExecuteSequence' 1 | ForEach-Object { $_[0] })
Check 'MajorUpgrade: RemoveExistingProducts scheduled' ($seq -contains 'RemoveExistingProducts')

# --- directories ---
$dirs = @{}
foreach ($r in @(Query 'SELECT Directory, Directory_Parent, DefaultDir FROM Directory' 3)) { $dirs[$r[0]] = @{ Parent = $r[1]; Name = (LongName ($r[2].Split(':')[0])) } }
function DirPath([string]$id) {
    $parts = @(); $cur = $id; $guard = 0
    while ($cur -and $dirs.ContainsKey($cur) -and $guard++ -lt 20) {
        $d = $dirs[$cur]
        if (-not $d.Parent -or $d.Parent -eq 'TARGETDIR' -or $d.Name -eq 'SourceDir') { $parts = , $cur + $parts; $cur = $null; break }   # a root (standard) directory id
        $parts = , $d.Name + $parts; $cur = $d.Parent
    }
    ($parts -join '\')
}
$badDirs = @()
$underLocal = 0
foreach ($id in $dirs.Keys) {
    $p = DirPath $id
    if ($p -match '^(LocalAppDataFolder|.*LocalAppDataFolder)\\') {
        $underLocal++
        $rel = $p -replace '^.*?LocalAppDataFolder\\', ''
        $ok = ($rel -eq 'Programs') -or ($rel -eq 'Programs\Apodofos') -or ($rel -eq "Programs\Apodofos\$slug") -or ($rel -like "Programs\Apodofos\$slug\*")
        if (-not $ok) { $badDirs += $p }
    }
}
Check 'every directory under LocalAppData sits under Programs\Apodofos\<slug>' ($badDirs.Count -eq 0 -and $underLocal -ge 3) "($($badDirs -join '; '))"
$compDirs = @(Query 'SELECT Directory_ FROM Component' 1 | ForEach-Object { DirPath $_[0] } | Sort-Object -Unique)
$badComp = @($compDirs | Where-Object { $_ -notmatch "LocalAppDataFolder\\Programs\\Apodofos\\$([regex]::Escape($slug))(\\|$)" -and $_ -notmatch '^(ProgramMenuFolder|DesktopFolder)$' })
Check 'every component directory is the product folder, its subfolders or the shortcut folders' ($badComp.Count -eq 0) "($($badComp -join '; '))"

# --- registry ---
$reg = @(Query 'SELECT `Root`, `Key`, `Name` FROM `Registry`' 3)
$badReg = @($reg | Where-Object { $_[0] -ne '1' -or $_[1] -notmatch "^Software\\Apodofos\\$([regex]::Escape($slug))(\\|$)" })
Check "registry rows: $($reg.Count) rows, all HKCU Software\Apodofos\$slug[\...]" ($reg.Count -gt 0 -and $badReg.Count -eq 0) "($(($badReg | ForEach-Object { $_[1] }) -join '; '))"
$rmReg = @(Query 'SELECT `Root`, `Key` FROM `RemoveRegistry`' 2)
Check 'no RemoveRegistry rows outside the product key' (@($rmReg | Where-Object { $_[1] -notmatch "^Software\\Apodofos\\$([regex]::Escape($slug))(\\|$)" }).Count -eq 0)

# --- custom actions ---
$cas = @(Query 'SELECT Action, Type, Source, Target FROM CustomAction' 4)
$legacyItems = @('window.json', 'recent.json', 'EBWebView')
$caProblems = @()
$sawUserData = $false; $sawInstall = $false
foreach ($ca in $cas) {
    $t = $ca[3]
    if ($t -notmatch '(?i)rmdir|\bdel\b') { continue }   # only deleting actions matter here
    $m = [regex]::Match($t, '(?i)cmd\.exe\]?"?\s+/c\s+(rmdir|del)\s+((?:/\w\s+)*)"([^"]+)"\s*$')
    if (-not $m.Success) { $caProblems += "$($ca[0]): unrecognised delete command: $t"; continue }
    $verb = $m.Groups[1].Value.ToLowerInvariant(); $recursive = $m.Groups[2].Value -match '/s'; $path = $m.Groups[3].Value.TrimEnd('\')
    $ok = $false
    switch -Regex ($path) {
        # the default install folder only; a user-chosen folder ([APPLICATIONFOLDER], [INSTALLFOLDER]) falls through to "not allowed"
        "^\[LocalAppDataFolder\]Programs\\Apodofos\\$([regex]::Escape($slug))$" { $ok = ($verb -eq 'rmdir' -and $recursive); $sawInstall = $ok }
        '^\[ProgramsFolderUser\]Apodofos$' { $ok = ($verb -eq 'rmdir' -and -not $recursive) }               # shared parent: NON-recursive only
        '^\[LocalAppDataFolder\]Apodofos$' { $ok = ($verb -eq 'rmdir' -and -not $recursive) }               # shared parent: NON-recursive only
        "^\[LocalAppDataFolder\]Apodofos\\$([regex]::Escape($slug))$" { $ok = ($verb -eq 'rmdir' -and $recursive); $sawUserData = $ok }
        '^\[LocalAppDataFolder\]Apodofos\\(window\.json|recent\.json|EBWebView)$' {
            $ok = $legacy -and ($recursive -eq ($Matches[1] -eq 'EBWebView')) -and (($verb -eq 'rmdir') -eq ($Matches[1] -eq 'EBWebView'))
        }
    }
    if (-not $ok) { $caProblems += "$($ca[0]): not allowed -> $t" }
}
Check 'custom actions delete only the product folders / allowed legacy items (never the Apodofos root recursively)' ($caProblems.Count -eq 0) "`n    $($caProblems -join "`n    ")"
Check 'CleanInstallFolder removes only the default Programs\Apodofos\<slug> and CleanUserData removes only Apodofos\<slug>' ($sawInstall -and $sawUserData)
Check 'no deleting custom action uses a folder the user can choose (APPLICATIONFOLDER, INSTALLFOLDER, CLIFOLDER)' (@($cas | Where-Object { $_[3] -match '(?i)rmdir|\bdel\b' -and $_[3] -match '\[(APPLICATIONFOLDER|INSTALLFOLDER|CLIFOLDER)\]' }).Count -eq 0)
$legacyCas = @($cas | Where-Object { $_[3] -match 'Apodofos\\(window\.json|recent\.json|EBWebView)' })
Check "legacy flat cleanup present exactly when ApodofosMigrateLegacyData is true (found $($legacyCas.Count), expected $(if ($legacy) { 3 } else { 0 }))" ($legacyCas.Count -eq $(if ($legacy) { 3 } else { 0 }))
Check 'no custom action touches HKCU Software\Apodofos' (@($cas | Where-Object { $_[3] -match '(?i)reg(\.exe)?\s+delete' }).Count -eq 0)
$uninstSeq = @(Query 'SELECT Action, Condition FROM InstallExecuteSequence' 2 | Where-Object { $_[0] -like 'Clean*' })
Check 'cleanup actions are skipped during upgrades (UPGRADINGPRODUCTCODE guard)' ($uninstSeq.Count -gt 0 -and @($uninstSeq | Where-Object { $_[1] -notmatch 'NOT UPGRADINGPRODUCTCODE' }).Count -eq 0)

# --- shortcuts ---
$sc = @(Query 'SELECT Shortcut, Name, Target FROM Shortcut' 3)
Check 'shortcuts exist (Start menu and desktop)' ($sc.Count -ge 2)
Check "every shortcut targets $desktopExe" (@($sc | Where-Object { $_[2] -notmatch [regex]::Escape($desktopExe) }).Count -eq 0 -and $sc.Count -gt 0) "($(($sc | ForEach-Object { $_[2] }) -join '; '))"
Check "shortcut names = '$($ed.ApodofosProductName)'" (@($sc | Where-Object { (LongName $_[1]) -ne $ed.ApodofosProductName }).Count -eq 0 -and $sc.Count -gt 0)

# --- files ---
$files = @(Query 'SELECT File, Component_, FileName FROM File' 3 | ForEach-Object { [pscustomobject]@{ Id = $_[0]; Name = (LongName $_[2]) } })
$names = @($files | ForEach-Object { $_.Name })
Check "file $desktopExe present" ($names -contains $desktopExe)
Check "file $cliExe present" ($names -contains $cliExe)
if ($legacy) { Check 'file apodofos.cmd (legacy command shim) present' ($names -contains 'apodofos.cmd') }
else { Check 'no apodofos.cmd shim in an edition without legacy data' ($names -notcontains 'apodofos.cmd') }
Check 'no file named Apodofos.exe / apodofos.exe' (@($names | Where-Object { $_ -ieq 'apodofos.exe' }).Count -eq 0)
$cliDir = @(Query 'SELECT Component, Directory_ FROM Component' 2)
Check 'CLI files live in the cli folder' (@($cliDir | Where-Object { $_[1] -eq 'CLIFOLDER' }).Count -ge $(if ($legacy) { 2 } else { 1 }))

[void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
if ($fail) { Write-Host "$fail check(s) failed." -ForegroundColor Red; exit 1 } else { Write-Host 'All static MSI checks passed.' -ForegroundColor Green; exit 0 }
