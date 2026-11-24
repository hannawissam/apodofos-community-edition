// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json;

namespace Apodofos.Host;

public sealed record RecentItem(string Path, string Type, string Opened);

/// <summary>
/// The recent-reports list (paths only, newest first, at most <see cref="MaxItems"/>), persisted as
/// <c>recent.json</c>. On first use it imports the list that older versions kept inside <c>window.json</c>,
/// so existing installs lose nothing. Never throws: corrupt or unwritable settings are ignored.
/// </summary>
public sealed class RecentStore
{
    public const int MaxItems = 5;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _file;
    private readonly string _legacyFile;
    private readonly Func<DateTime> _today;
    private List<RecentItem> _items = [];

    public RecentStore(string dataDirectory, Func<DateTime>? today = null)
    {
        _file = Path.Combine(dataDirectory, "recent.json");
        _legacyFile = Path.Combine(dataDirectory, "window.json");
        _today = today ?? (() => DateTime.Now);
        Load();
    }

    public IReadOnlyList<RecentItem> Items => _items;

    public void Add(string path, string type)
    {
        _items.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new RecentItem(path, type, _today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Remove(string path)
    {
        _items.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private static List<RecentItem> Clean(IEnumerable<RecentItem>? items) =>
        (items ?? []).Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new RecentItem(r.Path, r.Type ?? "", r.Opened ?? "")).Take(MaxItems).ToList();

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _items = Clean(JsonSerializer.Deserialize<List<RecentItem>>(File.ReadAllText(_file), ReadOptions));
                return;
            }
            if (File.Exists(_legacyFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_legacyFile));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "Recent", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.Array } arr)
                {
                    _items = Clean(arr.Value.Deserialize<List<RecentItem>>(ReadOptions));
                    if (_items.Count > 0) Save();
                }
            }
        }
        catch { /* corrupt settings: start with an empty list */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, _file, overwrite: true); // atomic replace: a crash never leaves a half-written list
        }
        catch { /* best effort */ }
    }
}
