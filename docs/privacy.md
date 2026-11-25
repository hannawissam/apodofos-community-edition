# Apodofos Privacy Notice

Apodofos Report Analyzer, Community Edition (the "application")
Publisher: Wissam HANNA
Effective date: 2026-11-25

## In short
Apodofos Community Edition does not collect, transmit, sell or share any data. Your reports are analysed entirely on your own
computer. The application makes no network connections.

## 1. What the application does not do
* No telemetry, analytics, usage statistics or crash reports.
* No accounts, sign-in, licence checks or activation.
* No update checks. You update by installing a new release yourself.
* No cookies, advertising or tracking of any kind.
* No connection to an Oracle database or to any other server. The application only reads report files that you choose.
* The embedded interface is part of the program. It loads no fonts, scripts, images or styles from the internet, and it
  blocks navigation to any address other than its own built-in page. The command line tool (`apodofos-report`) and the HTML file
  it can export are equally self-contained.
* Documentation links (to docs.oracle.com) are never fetched by the application. When you select one, the application asks your
  operating system to open it in your default web browser (only `https` addresses on oracle.com are accepted), exactly as if
  you had typed it there; your browser, not Apodofos, then connects. "Copy link" copies the address instead, and nothing is
  opened.

## 2. What is stored on your computer
The application writes the following, only on your computer, under `%LOCALAPPDATA%\Apodofos\ReportAnalyzer` (older versions used `%LOCALAPPDATA%\Apodofos` directly; the application moves those files into the new folder the first time it starts):
* `window.json`: the window position, size and maximised state.
* `recent.json`: the list of up to five recently opened report files (the full file path, the report type and the date it was
  opened).
* The Microsoft Edge WebView2 profile folder (`%LOCALAPPDATA%\Apodofos\ReportAnalyzer\EBWebView`): the Windows desktop application shows its
  interface through the Microsoft Edge WebView2 Runtime, which keeps its working files there. The interface also stores two
  display preferences in it (light or dark theme, position of the divider between the report and the analysis).

Report contents are not copied into these files. The application keeps no other copy of your reports, and the analysis is held
in memory only while the application is open.

To remove this data: click **Clear list** on the start screen to empty the recent-reports list (the "x" next to an entry
removes one entry), or close the application and delete the folder `%LOCALAPPDATA%\Apodofos\ReportAnalyzer`. Uninstalling the
application with the Windows installer (MSI) deletes this folder too (and only this folder: settings of any other Apodofos product are left alone); a portable copy leaves it until you delete it yourself.

## 3. Your reports and what you export
AWR, ASH and Statspack reports can contain sensitive information, for example SQL text, host names, schema names and user
names. Apodofos never sends them anywhere. You are responsible for what you save, export or share: the "Save HTML" export and
the SQL scripts you generate contain information taken from your report. Check them, and remove sensitive information, before
you send them to anyone.

## 4. Microsoft Edge WebView2 Runtime
The Windows desktop application uses the Microsoft Edge WebView2 Runtime, a separate Microsoft component that is not part of
Apodofos. It is governed by Microsoft's own terms and privacy statement and by the diagnostic data settings of your Windows
installation. Apodofos does not enable, configure or send any data through it, and the application loads only its own built-in
page in it (no website is opened, and requests for new windows and for permissions such as camera, microphone or location
are refused).

## 5. The developer and GitHub
The developer receives no personal data from the application. The source code, releases and issue tracker are hosted on
GitHub (https://github.com/hannawissam/apodofos-community-edition) and are subject to GitHub's privacy statement. Anything you
post in an issue or a pull request is public: do not attach real reports, host names, schema names or SQL text.

## 6. Children
The application is a professional tool for database administrators and is not directed at children.

## 7. GDPR and your rights
Because the application sends no data to the developer, the developer processes no personal data through it: there is no
controller or processor relationship created by using the application, and requests to access, correct or delete data do not
apply to it. The data described in section 2 is on your computer and under your control. If you have a question, open an issue at
https://github.com/hannawissam/apodofos-community-edition/issues (without personal or confidential information).

## 8. Changes to this notice
Changes are tracked in the git history of this file. The effective date above is updated when the content changes.

## 9. Trademarks
Apodofos is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle. Oracle and all Oracle
product names are trademarks or registered trademarks of Oracle Corporation and/or its affiliates.
