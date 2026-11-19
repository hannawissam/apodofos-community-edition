// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using Apodofos.Core.Model;

namespace Apodofos.Core.Parsing;

public sealed class AwrFormatException(string message) : Apodofos.Core.Providers.ReportFormatException(message);

/// <summary>Detects the report format and dispatches to the right parser. Never touches the network.</summary>
public static class AwrReportLoader
{
    public static AwrReport LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("AWR report not found.", path);
        var content = File.ReadAllText(path, Encoding.UTF8);
        return LoadContent(content, path);
    }

    public static AwrReport LoadContent(string content, string sourceName = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        var head = content.Length > 20000 ? content[..20000] : content;

        if (head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            // AWR "active HTML" reports embed a compressed XML payload rendered by Oracle's online JavaScript.
            if (head.Contains("<report_repository", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("emexpress", StringComparison.OrdinalIgnoreCase) ||
                (head.Contains("<report ", StringComparison.OrdinalIgnoreCase) && head.Contains("db_version", StringComparison.OrdinalIgnoreCase)))
                throw new AwrFormatException(
                    "This looks like an AWR 'active-html' report, which needs Oracle's online viewer. " +
                    "Please generate a regular HTML or text report (awrrpt.sql, report type 'html' or 'text').");

            var report = AwrHtmlParser.Parse(content, sourceName);
            Validate(report);
            return report;
        }

        if (head.Contains("WORKLOAD REPOSITORY", StringComparison.OrdinalIgnoreCase))
        {
            var report = AwrTextParser.Parse(content, sourceName);
            Validate(report);
            return report;
        }

        throw new AwrFormatException("The file does not look like an Oracle AWR report (HTML or text format).");
    }

    private static void Validate(AwrReport report)
    {
        if (report.Tables.Count == 0)
            throw new AwrFormatException("No AWR tables could be read from this file.");
        if (string.IsNullOrEmpty(report.Info.DbName))
            report.ParseWarnings.Add("The database header table was not found; database name is unknown.");
        if (report.Info.ElapsedMinutes is null)
            report.ParseWarnings.Add("Snapshot elapsed time not found; load calculations may be incomplete.");
        if (report.Info.MajorVersion is { } v && v < 19)
            report.ParseWarnings.Add($"Report is from Oracle {report.Info.Release}. Rules are calibrated for 19c+; most still apply.");
    }
}
