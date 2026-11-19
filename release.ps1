# Builds the shippable Windows x64 artifacts into artifacts/release/ (git-ignored):
#   QuietLoad-Portable-<version>-win-x64.zip
#   QuietLoad-Setup-<version>-win-x64.msi
#   SHA256SUMS.txt
# Steps: tests -> publish.ps1 -> (optional signing) -> portable zip -> MSI (WiX 5 via NuGet, no global install) -> checksums.
# Idempotent: artifacts/release and artifacts/stage are rebuilt from scratch on every run. Fails loudly on any error.
# Signing runs only when QUIETLOAD_SIGN_CERT_PATH is set (see docs/release.md); otherwise artifacts are UNSIGNED.
param(
    [switch]$SkipTests,
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot

function Invoke-Native([string]$What, [scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

# --- version from Directory.Build.props (single source of truth) ---
[xml]$props = Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { throw "Could not read a MAJOR.MINOR.PATCH <Version> from Directory.Build.props (got '$version')." }
Write-Host "== QuietLoad release $version ($Runtime) =="

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$stage     = Join-Path $artifacts 'stage'
$release   = Join-Path $artifacts 'release'
foreach ($p in $stage, $release, (Join-Path $artifacts 'installer-build'), (Join-Path $artifacts 'installer-obj')) {
    if (Test-Path $p) { Remove-Item -Recurse -Force $p }
}
New-Item -ItemType Directory -Force $stage, $release | Out-Null

# --- 1. tests ---
if ($SkipTests) { Write-Warning 'Tests skipped (-SkipTests).' }
else {
    Invoke-Native 'dotnet build' { dotnet build QuietLoad.slnx -c Release --nologo }
    Invoke-Native 'dotnet test'  { dotnet test QuietLoad.slnx -c Release --no-build --nologo }
}

# --- 2. publish ---
Invoke-Native 'publish.ps1' { & (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime }
$desktop = Join-Path $artifacts 'desktop'
$cli     = Join-Path $artifacts 'cli'
foreach ($f in "$desktop\QuietLoad.exe", "$cli\quietload.exe") { if (-not (Test-Path $f)) { throw "Missing published file $f" } }

# --- 3. optional code signing (placeholder; only runs when a certificate is configured) ---
function Invoke-CodeSign([string[]]$Files) {
    if (-not $env:QUIETLOAD_SIGN_CERT_PATH) { return }
    if (-not (Test-Path $env:QUIETLOAD_SIGN_CERT_PATH)) { throw "QUIETLOAD_SIGN_CERT_PATH points to a missing file: $env:QUIETLOAD_SIGN_CERT_PATH" }
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'QUIETLOAD_SIGN_CERT_PATH is set but signtool.exe was not found (install the Windows SDK).' }
    $ts = if ($env:QUIETLOAD_SIGN_TIMESTAMP_URL) { $env:QUIETLOAD_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    foreach ($f in $Files) {
        $a = @('sign', '/fd', 'SHA256', '/tr', $ts, '/td', 'SHA256', '/f', $env:QUIETLOAD_SIGN_CERT_PATH, '/d', 'QuietLoad Report Analyzer')
        if ($env:QUIETLOAD_SIGN_CERT_PASSWORD) { $a += @('/p', $env:QUIETLOAD_SIGN_CERT_PASSWORD) }
        Invoke-Native "signtool sign $f" { & $signtool @a $f }
    }
    Write-Host "Signed $($Files.Count) file(s)."
}
if ($env:QUIETLOAD_SIGN_CERT_PATH) { Invoke-CodeSign @("$desktop\QuietLoad.exe", "$cli\quietload.exe") }
else { Write-Warning 'QUIETLOAD_SIGN_CERT_PATH not set: artifacts will be UNSIGNED (Windows SmartScreen will warn).' }

# --- 4. staging layout shared by the zip and the MSI ---
#     (QuietLoad.exe and quietload.exe differ only by case, so the CLI lives in cli\)
Copy-Item "$desktop\*" $stage -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $stage 'cli') | Out-Null
Copy-Item "$cli\quietload.exe" (Join-Path $stage 'cli') -Force
foreach ($f in 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md') { if (-not (Test-Path (Join-Path $stage $f))) { throw "Missing $f in stage" } }
if (-not (Test-Path (Join-Path $stage 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt'))) { throw 'Missing .NET runtime notices (publish.ps1 could not find them).' }

# --- 5. portable zip ---
$portableDir = Join-Path $artifacts 'portable-src'
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
New-Item -ItemType Directory -Force $portableDir | Out-Null
Copy-Item "$stage\*" $portableDir -Recurse -Force
(Get-Content (Join-Path $PSScriptRoot 'installer\README-PORTABLE.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $portableDir 'README-PORTABLE.txt') -Encoding utf8
$zip = Join-Path $release "QuietLoad-Portable-$version-$Runtime.zip"
Compress-Archive -Path "$portableDir\*" -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $portableDir

# --- 6. licence page for the installer (WiX needs RTF; AGPL text is converted verbatim) ---
$text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LICENSE')) -replace "`r`n", "`n"
$sb = [Text.StringBuilder]::new('{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}\f0\fs18 ')
foreach ($ch in $text.ToCharArray()) {
    switch ($ch) {
        '\' { [void]$sb.Append('\\') }
        '{' { [void]$sb.Append('\{') }
        '}' { [void]$sb.Append('\}') }
        "`n" { [void]$sb.Append("\par`n") }
        default { if ([int]$ch -gt 126) { [void]$sb.Append('\u' + [int]$ch + '?') } else { [void]$sb.Append($ch) } }
    }
}
[void]$sb.Append('}')
[IO.File]::WriteAllText((Join-Path $stage 'LICENSE.rtf'), $sb.ToString(), [Text.Encoding]::ASCII)

# --- 7. MSI (WiX Toolset 5.0.2 SDK restored from NuGet; nothing is installed system-wide) ---
Invoke-Native 'WiX build' { dotnet build (Join-Path $PSScriptRoot 'installer\QuietLoad.Installer.wixproj') -c Release --nologo -p:StageDir="$stage\" -v:minimal }
$msiBuilt = Join-Path $artifacts "installer-build\QuietLoad-Setup-$version-$Runtime.msi"
if (-not (Test-Path $msiBuilt)) { throw "MSI not produced at $msiBuilt" }
$msi = Join-Path $release (Split-Path $msiBuilt -Leaf)
Copy-Item $msiBuilt $msi -Force
if ($env:QUIETLOAD_SIGN_CERT_PATH) { Invoke-CodeSign @($msi) }

# --- 8. checksums ---
$sums = Join-Path $release 'SHA256SUMS.txt'
Get-ChildItem $release -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    '{0} *{1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content $sums -Encoding ascii

Write-Host ''
Get-ChildItem $release -File | ForEach-Object { '{0,12:N0} bytes  {1}' -f $_.Length, $_.Name }
Get-Content $sums
Write-Host "Release artifacts ready in $release"
