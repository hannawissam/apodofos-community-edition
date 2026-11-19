QuietLoad Report Analyzer __VERSION__ - portable edition (Windows x64)
======================================================================

QuietLoad is an offline analyzer for Oracle AWR, ASH and Statspack reports.
It never connects to a database or to the internet.

HOW TO RUN
  1. Extract this zip anywhere you can write to (for example C:\Tools\QuietLoad).
  2. Double-click QuietLoad.exe, then use "Open report" to load an AWR/ASH/Statspack
     report (HTML or text).
  3. Command line: open a terminal in the "cli" folder and run
         quietload.exe "report.html" --text
     Run "quietload.exe --help" for all options. The cli folder is separate only because
     the two programs differ by a single letter case (Windows treats the names as equal).

REQUIREMENTS
  * Windows 10 or 11, 64-bit. No .NET installation is needed (the runtime is included).
  * Microsoft Edge WebView2 Evergreen Runtime. It ships with Windows 11 and Microsoft Edge.
    On a machine without it (for example a clean Windows 10) install it manually from
    https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Standalone Installer).
    QuietLoad never downloads it or anything else.

NO INSTALLATION
  Nothing is written to the registry, the Start menu or PATH. Windows SmartScreen may warn
  about an unsigned program the first time: choose "More info" > "Run anyway" if you trust the
  source, and verify the download with SHA256SUMS.txt (Get-FileHash -Algorithm SHA256 <file>).

SETTINGS AND TEMPORARY FILES
  Settings (recent reports, preferences) live in %LOCALAPPDATA%\QuietLoad.
  The single-file executable unpacks native libraries to a per-user temporary folder on first start.

HOW TO REMOVE
  Delete the folder you extracted. To also remove your settings, delete
  %LOCALAPPDATA%\QuietLoad.

LICENCE
  QuietLoad is licensed under the GNU Affero General Public License v3.0 (see LICENSE).
  NOTICE, THIRD-PARTY-NOTICES.md and DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt list the
  attributions and third-party components. Source code: see the NOTICE file.
