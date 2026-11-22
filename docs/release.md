# Releasing Apodofos (Windows x64)

`release.ps1` at the repository root produces everything under `artifacts/release/` (git-ignored):

| File | What it is |
|------|------------|
| `Apodofos-Portable-<version>-win-x64.zip` | Unzip-and-run edition: `Apodofos.exe`, `cli\apodofos.exe`, `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`, `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`, `README-PORTABLE.txt` |
| `Apodofos-Setup-<version>-win-x64.msi` | Windows Installer package |
| `SHA256SUMS.txt` | SHA-256 of both files (`<hash> *<file>`) |

The version is read from `Directory.Build.props` (`<Version>`). The CLI sits in `cli\` because `Apodofos.exe` and `apodofos.exe` differ only by letter case, which Windows treats as the same file name.

## Release notes (update them for every release)

`RELEASE-NOTES.md` at the repository root lists every feature of the application and the changes of each release. Before building a release: bump `<Version>` in `Directory.Build.props`, add the new section (version, date, user-visible changes) at the top of "Release history", update "All features" if a capability changed, then use that section as the body of the GitHub release (plus downloads, SmartScreen and licence notes). Use only synthetic samples in the notes, never names from real reports.

## Build

```
./release.ps1              # build + test + publish + zip + MSI + checksums
./release.ps1 -SkipTests   # skip the build/test step (not for real releases)
```

The script is idempotent (it deletes and rebuilds `artifacts/release`, `artifacts/stage` and the installer build folders) and stops on the first error. Requirements: .NET 11 SDK and access to nuget.org on the first run (to restore the WiX SDK package and WebView2). Nothing is installed system-wide.

## Installer tool: WiX Toolset 5.0.2 (MSI)

Chosen because it is restored as a NuGet SDK package (`installer/Apodofos.Installer.wixproj`, `WixToolset.Sdk/5.0.2` plus `WixToolset.UI.wixext`), needs no admin install and no global tool, and produces a standard MSI that supports per-user and per-machine installs, in-place upgrades and enterprise deployment. Version 5 is MS-RL licensed; WiX 6/7 are under the Open Source Maintenance Fee EULA, so they are deliberately not used. The project is not in `Apodofos.slnx`, so `dotnet build Apodofos.slnx` is unaffected.

Installer behaviour (`installer/Package.wxs`):

* Wizard artwork: the Apodofos logo banner and the branded final-page panel are `installer/images/banner.bmp` (493x58) and `installer/images/dialog.bmp` (493x312), generated from `assets/apodofos-512.png` by `installer/make-wizard-images.ps1` (Windows PowerShell 7, System.Drawing; the .bmp files are committed). Re-run the script after changing the logo or brand colours. The `.msi` file icon in Explorer is always the default Windows Installer icon (a limit of the format); the wizard, Apps & features entry and shortcuts use the Apodofos icon.
* Wizard (WixUI_Advanced): welcome, **licence page showing the AGPL-3.0 text**, "Install for me only / for all users" (per-user is the default and needs no admin), folder, optional features, install.
* Optional features, off by default: **Desktop shortcut**, **Add apodofos (CLI) to PATH** (user PATH for per-user installs, system PATH for per-machine). A Start menu shortcut is always created.
* Entry in Apps & features / Programs and Features; uninstall removes everything the product put on the machine: the program folder (including any file left in it), shortcuts, the PATH entry, the installer registry key and the settings and WebView2 profile folder `%LOCALAPPDATA%\Apodofos`. An upgrade keeps that settings folder; only an uninstall deletes it. Settings of other Windows users on a shared PC are not touched.
* `MajorUpgrade`: installing a newer (or the same) version replaces the old one in place; installing an older one is refused.
* Microsoft Edge WebView2 Evergreen runtime check (registry `...\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`, machine and user). If missing, the installer stops with a message explaining how to download it manually. The installer never downloads or bundles it. `SKIPWEBVIEW2CHECK=1` bypasses the check.

Silent install (no wizard):

```
msiexec /i Apodofos-Setup-1.0.1-win-x64.msi /qn /l*v setup.log MSIINSTALLPERUSER=1 ALLUSERS=2
msiexec /i Apodofos-Setup-1.0.1-win-x64.msi /qn MSIINSTALLPERUSER=1 ALLUSERS=2 INSTALLFOLDER="D:\Tools\Apodofos" ADDLOCAL=Main,DesktopShortcutFeature,PathFeature
msiexec /i Apodofos-Setup-1.0.1-win-x64.msi /qn ALLUSERS=1          # per machine, run elevated
msiexec /x Apodofos-Setup-1.0.1-win-x64.msi /qn                     # uninstall
```

A silent per-user install defaults to `%LOCALAPPDATA%\Programs\Apodofos`.

## Signing (artifacts are UNSIGNED by default)

Unsigned executables and installers trigger Microsoft Defender SmartScreen ("Windows protected your PC") and an "Unknown publisher" prompt. Options:

* **OV code-signing certificate**: removes "Unknown publisher"; SmartScreen reputation builds up over downloads.
* **EV code-signing certificate**: stronger identity validation; the key lives on a hardware token or cloud HSM. Since 2024 Microsoft no longer gives EV certificates instant SmartScreen reputation: every signed file still builds reputation through downloads.
* **Azure Trusted Signing** (Artifact Signing): Microsoft-managed short-lived certificates, no key to protect, works from CI; use the `Azure.CodeSigning.Dll` signtool dlib or the `trusted-signing-cli`.

`release.ps1` has a signing step that runs only when `APODOFOS_SIGN_CERT_PATH` is set (PFX path); optional `APODOFOS_SIGN_CERT_PASSWORD` and `APODOFOS_SIGN_TIMESTAMP_URL` (default DigiCert). It signs `Apodofos.exe` and `apodofos.exe` before they are packaged, then the MSI, using `signtool.exe` (Windows SDK) with SHA-256 and an RFC 3161 timestamp. For a token/HSM or Azure Trusted Signing, adapt `Invoke-CodeSign` in `release.ps1`. Never commit certificates (`*.pfx` is not to be staged). Checksums are computed after signing.

## Verify a download

```
Get-FileHash -Algorithm SHA256 .\Apodofos-Setup-1.0.1-win-x64.msi
# compare with the matching line of SHA256SUMS.txt
```

## Tests performed on 1.0.0 (Windows 11, non-elevated user)

* Portable: unzipped to a temp folder, `Apodofos.exe` opened `tests/Apodofos.Tests/TestData/ash-19c-hot-event.html`; window title `ash-19c-hot-event.html — Apodofos Report Analyzer`, process responding; `cli\apodofos.exe --help` works.
* MSI, silent per-user (`MSIINSTALLPERUSER=1 ALLUSERS=2`, no admin), with a custom `INSTALLFOLDER`/`APPLICATIONFOLDER`, and with the default folder: files, Start menu shortcut, desktop shortcut and user PATH entry (when selected), uninstall registration present; app launched from the installed location; reinstall of the same package over itself succeeded; silent uninstall removed the folder, shortcuts, PATH entry, uninstall registration and installer registry keys. Only `%LOCALAPPDATA%\Apodofos` (user settings) remains.
* Not verified: the interactive wizard pages (licence page, scope page), per-machine (elevated) install, the WebView2-missing message (the runtime is present on the test machine), upgrade from an older released version (none exists yet), signing.
