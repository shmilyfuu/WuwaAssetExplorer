using WuwaAssetExplorer.Core.Models;

namespace WuwaAssetExplorer.Core.Services;

public sealed class AssetCatalog
{
    private AssetEntry[] _entries = [];
    private Dictionary<string, AssetEntry[]> _byName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Length;

    public void Replace(IEnumerable<string> paths)
    {
        var entries = paths
            .Select(AssetEntry.FromPath)
            .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var byName = entries
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        Volatile.Write(ref _entries, entries);
        Volatile.Write(ref _byName, byName);
    }

    public IReadOnlyList<AssetEntry> Search(string? query, int maxResults = 400)
    {
        if (maxResults <= 0) return [];
        var entries = Volatile.Read(ref _entries);
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(query)) return [];

        var trimmed = query.Trim();
        var result = new List<AssetEntry>(Math.Min(maxResults, 128));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var byName = Volatile.Read(ref _byName);
        if (byName.TryGetValue(trimmed, out var exact))
        {
            foreach (var item in exact)
            {
                if (seen.Add(item.FullPath)) result.Add(item);
                if (result.Count >= maxResults) return result;
            }
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (seen.Contains(entry.FullPath)) continue;

            var matches = true;
            foreach (var token in tokens)
            {
                if (!entry.FullPath.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches) continue;
            seen.Add(entry.FullPath);
            result.Add(entry);
            if (result.Count >= maxResults) break;
        }

        result.Sort(static (left, right) =>
        {
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
        });
        return result;
    }
}
