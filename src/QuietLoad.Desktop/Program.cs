// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

namespace QuietLoad.Desktop;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith('-'))));
    }

    static void ShowFatal(Exception? ex)
    {
        try
        {
            MessageBox.Show("An unexpected error occurred.\n\n" + ex?.Message, "QuietLoad Report Analyzer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* nothing more we can do */ }
    }
}
