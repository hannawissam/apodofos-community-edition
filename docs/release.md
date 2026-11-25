# Releasing Apodofos Performance Report Analyzer (Windows x64)

`release.ps1` at the repository root produces everything under `artifacts/release/` (git-ignored):

| File | What it is |
|------|------------|
| `Apodofos-PerformanceReportAnalyzer-Portable-<version>-win-x64.zip` | Unzip-and-run edition: `ApodofosPerformanceReportAnalyzer.exe`, `cli\ApodofosReportAnalyzer.exe`, `cli\apodofos.cmd` and `cli\apodofos-report.cmd` (forward the former command names of 1.0.x and 1.1.0), `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`, `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`, `README-PORTABLE.txt` |
| `Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi` | Windows Installer package (per-user install, no administrator rights) |
| `SHA256SUMS.txt` | SHA-256 of both files (`<hash> *<file>`) |

The version is read from `Directory.Build.props` (`<Version>`). The CLI sits in `cli\` next to the desktop program. All names (product name, folder, executables, release file prefix, installer upgrade code) come from `Edition.props`, the one file that differs between the Community and Professional builds: "Apodofos" is the brand, each product has its own folders, registry key, executables and installer identity, so the editions install side by side and removing one never touches the other. The Community upgrade code is unchanged since 1.0.x, so 1.0.x installs upgrade in place. Never respell, abbreviate or fuse the word "Apodofos" (pending trade mark).

## Release notes (update them for every release)

`RELEASE-NOTES.md` at the repository root lists every feature of the application and the changes of each release. Before building a release: bump `<Version>` in `Directory.Build.props`, add the new section (version, date, user-visible changes) at the top of "Release history", update "All features" if a capability changed, then use that section as the body of the GitHub release (plus downloads, SmartScreen and licence notes). Use only synthetic samples in the notes, never names from real reports.

## Build

```
./release.ps1              # build + test + publish + zip + MSI + checksums
./release.ps1 -SkipTests   # skip the build/test step (not for real releases)
./release.ps1 -MsiOnly     # rebuild ONLY the MSI and SHA256SUMS.txt from the existing build of the same version
./release.ps1 -SkipTests -ArtifactsDir <folder>   # build into another folder (checks, experiments)
```

After building, validate the MSI statically (no install): `pwsh tools/verify-msi.ps1 -Msi artifacts\release\Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi` (identity, folders, registry, uninstall cleanup scope, shortcuts, files). The real install, upgrade and uninstall test is `tools/test-installer.ps1` (it deletes the product's settings folder, so run it on a machine where that is acceptable).

`-MsiOnly` is for re-issuing an installer fix without rebuilding the application: it needs the existing `artifacts/stage` and `artifacts/release` of the same `<Version>` (it stops with a clear message if they are missing), skips the tests and the publish, and leaves the application files and the portable zip untouched (the zip keeps its checksum). It takes about a minute.

The script is idempotent (it deletes and rebuilds `artifacts/release`, `artifacts/stage` and the installer build folders) and stops on the first error. Requirements: .NET 11 SDK and access to nuget.org on the first run (to restore the WiX SDK package and WebView2). Nothing is installed system-wide.

## Installer tool: WiX Toolset 5.0.2 (MSI)

Chosen because it is restored as a NuGet SDK package (`installer/Apodofos.Installer.wixproj`, `WixToolset.Sdk/5.0.2` plus `WixToolset.UI.wixext`), needs no admin install and no global tool, and produces a standard MSI that installs per user only (no administrator rights and no UAC prompt), with in-place upgrades. Version 5 is MS-RL licensed; WiX 6/7 are under the Open Source Maintenance Fee EULA, so they are deliberately not used. The project is not in `Apodofos.slnx`, so `dotnet build Apodofos.slnx` is unaffected.

Installer behaviour (`installer/Package.wxs`):

* Wizard artwork: the Apodofos logo banner and the branded final-page panel are `installer/images/banner.bmp` (493x58) and `installer/images/dialog.bmp` (493x312), generated from `assets/apodofos-512.png` by `installer/make-wizard-images.ps1` (Windows PowerShell 7, System.Drawing; the .bmp files are committed). Re-run the script after changing the logo or brand colours. The `.msi` file icon in Explorer is always the default Windows Installer icon (a limit of the format); the wizard, Apps & features entry and shortcuts use the Apodofos icon.
* Wizard (WixUI_FeatureTree): welcome page, **licence page showing the AGPL-3.0 text**, custom setup (optional features and the install folder), ready to install, install, final page. There is no "install for all users" page: the installer is per-user only. The default folder is `%LOCALAPPDATA%\Programs\Apodofos\PerformanceReportAnalyzer`, in the wizard and in silent installs alike.
* Optional features, off by default: **Desktop shortcut**, **Add ApodofosReportAnalyzer (CLI) to PATH** (added to the current user's PATH). A Start menu shortcut is always created.
* Entry in Apps & features / Programs and Features; uninstall removes everything the product put on the machine: the program folder (including any file left in it), shortcuts, the PATH entry, the installer registry key and the settings and WebView2 profile folder `%LOCALAPPDATA%\Apodofos\PerformanceReportAnalyzer`. Because the Community edition also claims the data of earlier releases, an uninstall additionally removes the three flat 1.0.x items (`window.json`, `recent.json`, `EBWebView` in `%LOCALAPPDATA%\Apodofos`) and the 1.1.0 folder `%LOCALAPPDATA%\Apodofos\ReportAnalyzer` (that folder only); the shared `Apodofos` parents are removed only when empty, and other products' folders (for example the Professional edition) are never touched. An upgrade keeps all of it (the application moves older data into the new folder on first start); only an uninstall deletes it. Settings of other Windows users on a shared PC are not touched. The uninstall also works while the application is running (tested): Windows Installer closes it and nothing is left behind.
* `MajorUpgrade`: installing a newer (or the same) version replaces the old one in place; installing an older one is refused.
* Microsoft Edge WebView2 Evergreen runtime check (registry `...\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`, machine and user). If missing, the installer stops with a message explaining how to download it manually. The installer never downloads or bundles it. `SKIPWEBVIEW2CHECK=1` bypasses the check.

Silent install (no wizard):

```
msiexec /i Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi /qn /l*v setup.log
msiexec /i Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi /qn INSTALLFOLDER="D:\Tools\Apodofos\PerformanceReportAnalyzer" ADDLOCAL=Main,DesktopShortcutFeature,PathFeature
msiexec /x Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi /qn                     # uninstall
```

Without `INSTALLFOLDER` the install goes to `%LOCALAPPDATA%\Programs\Apodofos\PerformanceReportAnalyzer` (settings and the WebView2 profile are in `%LOCALAPPDATA%\Apodofos\PerformanceReportAnalyzer`, the installer registry key in `HKCU\Software\Apodofos\PerformanceReportAnalyzer`; uninstall removes only these, never the shared `Apodofos` parents unless they are empty). A silent upgrade resets the optional features unless `ADDLOCAL` is passed again. All installs are per user, so none of these commands needs an elevated prompt. (Old command lines that still pass `MSIINSTALLPERUSER=1 ALLUSERS=2` keep working: the properties are ignored.)

Upgrades: installing this MSI over a 1.1.0 install (`Programs\Apodofos\ReportAnalyzer`) or an older 1.0.x install (flat `Programs\Apodofos`) replaces it in place (same upgrade code; the old product is removed first, its uninstall cleanup is skipped during the upgrade so the settings stay for the application's first-start migration). The command names of both earlier generations keep working through `cli\apodofos.cmd` and `cli\apodofos-report.cmd`. Details for 1.0.x: installing this MSI over an older 1.0.x MSI replaces it in place, whether the older one was installed by the old wizard (`%LOCALAPPDATA%\Apps\Apodofos`) or silently (`%LOCALAPPDATA%\Programs\Apodofos`); the old copy is removed and one entry remains in Apps & features. An older all-users (per-machine) install cannot be replaced without an elevated prompt: uninstall that entry once from Settings, Apps, then install the new MSI.

## Signing (artifacts are UNSIGNED by default)

Unsigned executables and installers trigger Microsoft Defender SmartScreen ("Windows protected your PC") and an "Unknown publisher" prompt. Options:

* **OV code-signing certificate**: removes "Unknown publisher"; SmartScreen reputation builds up over downloads.
* **EV code-signing certificate**: stronger identity validation; the key lives on a hardware token or cloud HSM. Since 2024 Microsoft no longer gives EV certificates instant SmartScreen reputation: every signed file still builds reputation through downloads.
* **Azure Trusted Signing** (Artifact Signing): Microsoft-managed short-lived certificates, no key to protect, works from CI; use the `Azure.CodeSigning.Dll` signtool dlib or the `trusted-signing-cli`.

`release.ps1` has a signing step that runs only when `APODOFOS_SIGN_CERT_PATH` is set (PFX path); optional `APODOFOS_SIGN_CERT_PASSWORD` and `APODOFOS_SIGN_TIMESTAMP_URL` (default DigiCert). It signs `ApodofosPerformanceReportAnalyzer.exe` and `apodofos-report.exe` before they are packaged, then the MSI, using `signtool.exe` (Windows SDK) with SHA-256 and an RFC 3161 timestamp. For a token/HSM or Azure Trusted Signing, adapt `Invoke-CodeSign` in `release.ps1`. Never commit certificates (`*.pfx` is not to be staged). Checksums are computed after signing.

## Verify a download

```
Get-FileHash -Algorithm SHA256 .\Apodofos-PerformanceReportAnalyzer-Setup-<version>-win-x64.msi
# compare with the matching line of SHA256SUMS.txt
```

> The test records below are history: they describe the 1.0.x file names and folders (`Apodofos.exe`, `apodofos.exe`, `Programs\Apodofos`). 1.1.0 and 1.2.0 renamed them (see above and `RELEASE-NOTES.md`).

## Tests performed on 1.0.0 (Windows 11, non-elevated user)

* Portable: unzipped to a temp folder, `Apodofos.exe` opened `tests/Apodofos.Tests/TestData/ash-19c-hot-event.html`; window title `ash-19c-hot-event.html — Apodofos Performance Report Analyzer`, process responding; `cli\apodofos.exe --help` works.
* MSI, silent per-user (`MSIINSTALLPERUSER=1 ALLUSERS=2`, no admin), with a custom `INSTALLFOLDER`/`APPLICATIONFOLDER`, and with the default folder: files, Start menu shortcut, desktop shortcut and user PATH entry (when selected), uninstall registration present; app launched from the installed location; reinstall of the same package over itself succeeded; silent uninstall removed the folder, shortcuts, PATH entry, uninstall registration and installer registry keys. Only `%LOCALAPPDATA%\Apodofos` (user settings) remains.
* Not verified: the interactive wizard pages (licence page, scope page), per-machine (elevated) install, the WebView2-missing message (the runtime is present on the test machine), upgrade from an older released version (none exists yet), signing.

## Tests performed on the per-user MSI with the branded wizard (22 Nov 2026, Windows 11, non-elevated user)

Application files unchanged (same `Apodofos.exe` and `apodofos.exe` as the published 1.0.7, and the portable zip byte-identical); only the MSI was rebuilt with `release.ps1 -MsiOnly`.

* Wizard (real pages): welcome, licence, custom setup, ready to install and final page all show the Apodofos artwork; a default install went to `%LOCALAPPDATA%\Programs\Apodofos`, and the uninstall left nothing behind.
* Upgrade over the originally published 1.0.7 MSI, silently installed in `Programs\Apodofos` and in `Apps\Apodofos`: both replaced by one new install in `Programs\Apodofos`, one Apps & features entry, shortcuts and PATH entry kept; uninstall removed everything (folder, shortcuts, PATH, registry key, the empty `Apps` parent folder).
* Fresh silent install, application launched on a synthetic report, uninstall while it was still running: the application was closed, and the program folder, shortcut, uninstall entry, `HKCU\Software\Apodofos` and the settings and WebView2 folder `%LOCALAPPDATA%\Apodofos` were all removed.
* Strict residue scan of the whole user profile and registry (install, use, uninstall, with and without the application running) found only records Windows itself writes for any program (recent programs, app list backup, shell history) and short-lived temporary files.
* Not verified: an upgrade of an older all-users (per-machine) install, the WebView2-missing message, signing.
