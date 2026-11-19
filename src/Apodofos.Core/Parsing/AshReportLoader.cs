// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;

namespace Apodofos.Core.Parsing;

/// <summary>Loads an ASH report (HTML or text) into the generic table model. Never touches the network.</summary>
public static class AshReportLoader
{
    public static AwrReport LoadContent(string content, string sourceName = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        var head = content.Length > 20000 ? content[..20000] : content;
        AwrReport report;
        if (head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            report = AwrHtmlParser.Parse(content, sourceName);
            using (NumberParser.Use(report.NumberConvention)) // the header cells are read the way this report prints numbers
                AshInfoExtractor.Populate(report);
        }
        else
            report = AshTextParser.Parse(content, sourceName);

        report.Kind = ReportKind.Ash;
        if (report.Tables.Count == 0)
            throw new AwrFormatException("No ASH tables could be read from this file.");
        if (string.IsNullOrEmpty(report.Info.DbName))
            report.ParseWarnings.Add("The ASH database header table was not found; database name is unknown.");
        if (report.Info.ElapsedMinutes is null)
            report.ParseWarnings.Add("ASH elapsed time not found; load calculations may be incomplete.");
        if (report.Info.AshSampleCount is null)
            report.ParseWarnings.Add("ASH sample count not found; shares are taken from the report's own percentages.");
        if (report.Info.MajorVersion is { } v && v < 11)
            report.ParseWarnings.Add($"Report is from Oracle {report.Info.Release}; some ASH sections (for example Top Blocking Sessions details) are not available before 11g.");
        return report;
    }
}
