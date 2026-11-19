// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>Fills <see cref="AwrReportInfo"/> from the header tables (same shape in HTML and text reports).</summary>
internal static class AwrInfoExtractor
{
    public static void Populate(AwrReport report)
    {
        var info = report.Info;

        var db = report.TableWithColumns("DB Name", "Release") ?? report.Tables.FirstOrDefault(t => t.HasColumn("DB Name"));
        if (db?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } dbRow)
        {
            info.DbName = dbRow.Str("DB Name");
            info.DbId = dbRow.Str("DB Id");
            info.InstanceName = dbRow.Str("Instance");
            info.InstanceNumber = (int)(dbRow.Num("Inst num", "Inst Num") ?? 0);
            info.StartupTime = dbRow.Str("Startup Time");
            info.Release = dbRow.Str("Release");
            info.IsRac = dbRow.Str("RAC").Equals("YES", StringComparison.OrdinalIgnoreCase);
            var cdb = dbRow.Str("CDB");
            if (cdb.Length > 0) info.IsCdb = cdb.Equals("YES", StringComparison.OrdinalIgnoreCase);
        }

        var host = report.TableWithColumns("Host Name", "Platform");
        if (host?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } h)
        {
            info.HostName = h.Str("Host Name");
            info.Platform = h.Str("Platform");
            info.Cpus = (int?)h.Num("CPUs");
            info.Cores = (int?)h.Num("Cores");
            info.Sockets = (int?)h.Num("Sockets");
            info.MemoryGb = h.Num("Memory (GB)", "Memory");
        }

        var snap = report.TableWithColumns("Snap Id", "Snap Time");
        if (snap != null)
        {
            if (snap.HasColumn("CDB") && info.IsCdb is null)
            {
                var c = snap.RowByLabel("Begin Snap")?.Str("CDB");
                if (!string.IsNullOrEmpty(c)) info.IsCdb = c.Equals("YES", StringComparison.OrdinalIgnoreCase);
            }
            if (snap.RowByLabel("Begin Snap") is { } b)
            {
                info.BeginSnapId = b.Str("Snap Id");
                info.BeginSnapTime = b.Str("Snap Time");
                info.BeginSessions = b.Num("Sessions");
            }
            if (snap.RowByLabel("End Snap") is { } e)
            {
                info.EndSnapId = e.Str("Snap Id");
                info.EndSnapTime = e.Str("Snap Time");
                info.EndSessions = e.Num("Sessions");
            }
            info.ElapsedMinutes = FirstNumeric(snap.RowByLabel("Elapsed"));
            info.DbTimeMinutes = FirstNumeric(snap.RowByLabel("DB Time"));
        }

        // Fallbacks from the Load Profile / Time Model when the snapshot table is missing.
        if (info.Cpus is null)
        {
            var os = report.Table("Operating System Statistics");
            var cpus = os?.RowByLabel("NUM_CPUS")?.NumAt(1);
            if (cpus != null) info.Cpus = (int)cpus;
        }
    }

    private static double? FirstNumeric(AwrRow? row)
    {
        if (row is not { } r) return null;
        for (var i = 1; i < r.Cells.Length; i++)
        {
            var v = NumberParser.ParseMinutes(r.Cells[i]); // honours "(mins)", "(Min.)", "(Std.)" ...
            if (v != null) return v;
        }
        return null;
    }
}
