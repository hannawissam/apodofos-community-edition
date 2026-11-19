// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

namespace Apodofos.Core.Model;

public enum AwrReportFormat { Html, Text }

/// <summary>Which Oracle report family a parsed report belongs to; rules declare the kinds they apply to.</summary>
public enum ReportKind { Awr, Ash, Statspack }

/// <summary>Header facts of the report (database, host and snapshot window).</summary>
public sealed class AwrReportInfo
{
    public string DbName { get; set; } = "";
    public string DbId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public int InstanceNumber { get; set; }
    public string StartupTime { get; set; } = "";
    public string Release { get; set; } = "";
    public bool IsRac { get; set; }
    public bool? IsCdb { get; set; }
    public string HostName { get; set; } = "";
    public string Platform { get; set; } = "";
    public int? Cpus { get; set; }
    public int? Cores { get; set; }
    public int? Sockets { get; set; }
    public double? MemoryGb { get; set; }
    public string BeginSnapId { get; set; } = "";
    public string EndSnapId { get; set; } = "";
    public string BeginSnapTime { get; set; } = "";
    public string EndSnapTime { get; set; } = "";
    public double? BeginSessions { get; set; }
    public double? EndSessions { get; set; }
    /// <summary>Snapshot interval in minutes.</summary>
    public double? ElapsedMinutes { get; set; }
    /// <summary>Total DB Time in minutes.</summary>
    public double? DbTimeMinutes { get; set; }

    // ---- ASH report header facts (null for AWR / Statspack) ----
    /// <summary>"Sample Count" of an ASH report.</summary>
    public double? AshSampleCount { get; set; }
    /// <summary>"Avg. Active Session per CPU" of an ASH report.</summary>
    public double? AshAasPerCpu { get; set; }
    /// <summary>"Data Source" of an ASH report (V$ACTIVE_SESSION_HISTORY or DBA_HIST_ACTIVE_SESS_HISTORY).</summary>
    public string AshDataSource { get; set; } = "";
    /// <summary>"Report Target" of an ASH report (None specified, or the SQL_ID / session / module filter).</summary>
    public string AshReportTarget { get; set; } = "";
    /// <summary>True when the ASH sample window came from DBA_HIST (one sample per 10 seconds) rather than V$ASH (one per second).</summary>
    public bool AshFromHistory { get; set; }

    /// <summary>Average Active Sessions = DB Time / Elapsed Time (Oracle's key load metric).</summary>
    public double? AverageActiveSessions =>
        ElapsedMinutes is > 0 && DbTimeMinutes is not null ? DbTimeMinutes / ElapsedMinutes : null;

    /// <summary>Major version, e.g. 19 for "19.0.0.0.0", 12 for "12.1.0.2.0".</summary>
    public int? MajorVersion
    {
        get
        {
            var part = Release.Split('.', 2)[0];
            return int.TryParse(part, out var v) ? v : null;
        }
    }
}

/// <summary>Parsed AWR report: header info plus every table found in the report.</summary>
public sealed class AwrReport
{
    public string SourcePath { get; set; } = "";
    public AwrReportFormat Format { get; set; }
    /// <summary>Report family (AWR by default); set by the provider that parsed the report.</summary>
    public ReportKind Kind { get; set; } = ReportKind.Awr;
    public AwrReportInfo Info { get; set; } = new();
    public List<AwrTable> Tables { get; } = [];
    /// <summary>Report HTML with scripts/external links removed and table ids injected (for display).</summary>
    public string SanitizedHtml { get; set; } = "";
    public List<string> ParseWarnings { get; } = [];
    /// <summary>Number convention detected from the report's own cells (see <see cref="Parsing.NumberConventions"/>).</summary>
    public Parsing.NumberConvention NumberConvention { get; set; } = Parsing.NumberConvention.Invariant;
    /// <summary>True when the report mixes both conventions without a clear majority: numbers cannot be trusted.</summary>
    public bool NumberConventionAmbiguous { get; set; }
    /// <summary>Plain statement of the detected format, e.g. "Numbers read as 1.234,56 (comma decimal, dot thousands)."</summary>
    public string NumberFormatNote { get; set; } = "Numbers read as 1,234.56 (dot decimal, comma thousands).";
    public (int European, int Invariant) NumberVotes { get; set; }
    /// <summary>True when the report is cut off (the "End of Report" marker is missing).</summary>
    public bool Truncated { get; set; }
    public string TruncationReason { get; set; } = "";
    /// <summary>Notes about a damaged or partial report (truncation); consistency checks are added during analysis.</summary>
    public List<string> IntegrityWarnings { get; } = [];

    public AwrTable? Table(params string[] exactTitles)
    {
        foreach (var t in exactTitles)
        {
            var hit = Tables.FirstOrDefault(x => x.TitleIs(t));
            if (hit != null) return hit;
        }
        return null;
    }

    public AwrTable? TableStartingWith(params string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            var hit = Tables.FirstOrDefault(x => x.TitleStartsWith(p));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>First table whose headers contain all the given columns.</summary>
    public AwrTable? TableWithColumns(params string[] columns) =>
        Tables.FirstOrDefault(t => columns.All(c => t.Headers.Any(h => AwrTable.Normalize(h) == AwrTable.Normalize(c))));

    /// <summary>Instance Efficiency is a 4-column key/value grid; this flattens it.</summary>
    public Dictionary<string, double> EfficiencyPercentages()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var t = TableStartingWith("Instance Efficiency");
        if (t == null) return result;
        foreach (var r in t.Rows)
            for (var i = 0; i + 1 < r.Length; i += 2)
            {
                var key = AwrTable.Normalize(r[i]);
                var v = Parsing.NumberParser.Parse(r[i + 1]);
                if (key.Length > 0 && v is not null) result[key] = v.Value;
            }
        return result;
    }

    /// <summary>init.ora parameter name -> begin value (lower-case names).</summary>
    public Dictionary<string, string> InitParameters()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var t = Table("init.ora Parameters");
        if (t == null) return result;
        var nameCol = t.Col("Parameter Name");
        var valCol = t.Col("Begin value", "Begin Value");
        if (nameCol < 0) nameCol = 0;
        if (valCol < 0) valCol = 1;
        var pdbCol = t.Col("PDB Name");
        // In a CDB report a parameter appears once for the CDB (blank PDB column) and again per PDB:
        // the CDB-level value wins, PDB rows only fill in names not seen yet.
        foreach (var r in t.Rows)
        {
            if (r.Length <= Math.Max(nameCol, valCol) || r[nameCol].Length == 0) continue;
            var name = r[nameCol].Trim();
            var isPdbRow = pdbCol >= 0 && pdbCol < r.Length && r[pdbCol].Trim().Length > 0;
            if (isPdbRow && result.ContainsKey(name)) continue;
            result[name] = r[valCol].Trim();
        }
        return result;
    }

    /// <summary>SQL Id -> SQL text, from "Complete List of SQL Text".</summary>
    public Dictionary<string, string> SqlTexts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var t = Table("Complete List of SQL Text");
        if (t == null) return result;
        var idCol = t.Col("SQL Id");
        var txtCol = t.Col("SQL Text");
        if (idCol < 0 || txtCol < 0) return result;
        foreach (var r in t.Rows)
            if (r.Length > Math.Max(idCol, txtCol)) result[r[idCol].Trim()] = r[txtCol];
        return result;
    }
}
