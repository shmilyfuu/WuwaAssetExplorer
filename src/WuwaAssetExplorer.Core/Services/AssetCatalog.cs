using System.Collections.Concurrent;
using WuwaAssetExplorer.Core.Models;

namespace WuwaAssetExplorer.Core.Services;

public sealed class AssetCatalog
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "uasset", "umap", "uexp", "ubulk", "uptnl"
    };

    private AssetEntry[] _entries = [];
    private Dictionary<string, AssetEntry[]> _byName = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, AssetDirectorySnapshot> _directoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => Volatile.Read(ref _entries).Length;

    public void Replace(IEnumerable<string> paths)
    {
        var entries = paths
            .Select(AssetEntry.FromPath)
            .OrderBy(x => x.FullPath, PathComparer)
            .ToArray();

        var byName = entries
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        Volatile.Write(ref _entries, entries);
        Volatile.Write(ref _byName, byName);
        Volatile.Write(
            ref _directoryCache,
            new ConcurrentDictionary<string, AssetDirectorySnapshot>(StringComparer.OrdinalIgnoreCase));
    }

    public AssetDirectorySnapshot Browse(string? directoryPath)
    {
        var normalized = NormalizeDirectoryPath(directoryPath);
        var cache = Volatile.Read(ref _directoryCache);
        return cache.GetOrAdd(normalized, BuildDirectorySnapshot);
    }

    public IReadOnlyList<AssetBrowserEntry> Search(string? query, int maxResults = 400)
    {
        if (maxResults <= 0) return [];
        var entries = Volatile.Read(ref _entries);
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(query)) return [];

        var trimmed = query.Trim();
        var byName = Volatile.Read(ref _byName);

        // A complete asset name is the common case when following a resource reference.
        // Returning the exact logical resource here avoids another full scan across 2M+ paths.
        if (byName.TryGetValue(trimmed, out var exact))
        {
            return BuildResourceEntries(exact, maxResults);
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = new Dictionary<string, ResourceAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
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

            var key = LogicalResourceKey(entry);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                if (groups.Count >= maxResults) break;
                accumulator = new ResourceAccumulator(entry, key);
                groups.Add(key, accumulator);
            }
            else
            {
                accumulator.Add(entry);
            }
        }

        return FinalizeResources(groups.Values, maxResults);
    }

    private AssetDirectorySnapshot BuildDirectorySnapshot(string directoryPath)
    {
        var entries = Volatile.Read(ref _entries);
        if (entries.Length == 0)
        {
            return new AssetDirectorySnapshot(directoryPath, [], 0, 0);
        }

        var prefix = string.IsNullOrEmpty(directoryPath) ? string.Empty : directoryPath + "/";
        var start = string.IsNullOrEmpty(prefix) ? 0 : LowerBound(entries, prefix);
        var end = string.IsNullOrEmpty(prefix) ? entries.Length : PrefixEnd(entries, prefix);

        var directories = new List<AssetBrowserEntry>();
        var directFiles = new List<AssetEntry>();

        var index = start;
        while (index < end)
        {
            var entry = entries[index];
            if (!string.IsNullOrEmpty(prefix) &&
                !entry.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var remainder = entry.FullPath.AsSpan(prefix.Length);
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
            {
                directFiles.Add(entry);
                index++;
                continue;
            }

            var childName = remainder[..slashIndex].ToString();
            var childPath = string.IsNullOrEmpty(prefix)
                ? childName
                : prefix + childName;

            directories.Add(AssetBrowserEntry.Directory(childName, childPath));

            // Every entry below this child directory is contiguous in the sorted catalog.
            // Jump to the end of that range so opening the root does not linearly walk all 2M+ files.
            var childPrefix = childPath + "/";
            var nextIndex = PrefixEnd(entries, childPrefix);
            index = Math.Max(index + 1, nextIndex);
        }

        var resources = BuildResourceEntries(directFiles, int.MaxValue);
        var items = new List<AssetBrowserEntry>(directories.Count + resources.Count);
        items.AddRange(directories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
        items.AddRange(resources);

        return new AssetDirectorySnapshot(
            directoryPath,
            items,
            directories.Count,
            resources.Count);
    }

    private static IReadOnlyList<AssetBrowserEntry> BuildResourceEntries(
        IEnumerable<AssetEntry> entries,
        int maxResults)
    {
        var groups = new Dictionary<string, ResourceAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var key = LogicalResourceKey(entry);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                if (groups.Count >= maxResults) break;
                accumulator = new ResourceAccumulator(entry, key);
                groups.Add(key, accumulator);
            }
            else
            {
                accumulator.Add(entry);
            }
        }

        return FinalizeResources(groups.Values, maxResults);
    }

    private static IReadOnlyList<AssetBrowserEntry> FinalizeResources(
        IEnumerable<ResourceAccumulator> groups,
        int maxResults)
    {
        return groups
            .Select(x => x.ToBrowserEntry())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    private static string LogicalResourceKey(AssetEntry entry)
    {
        if (!PackageExtensions.Contains(entry.Extension)) return entry.FullPath;

        var dotIndex = entry.FullPath.LastIndexOf('.');
        return dotIndex > 0 ? entry.FullPath[..dotIndex] : entry.FullPath;
    }

    private static string NormalizeDirectoryPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('\\', '/').Trim('/');

    private static int LowerBound(AssetEntry[] entries, string value)
    {
        var low = 0;
        var high = entries.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (PathComparer.Compare(entries[middle].FullPath, value) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int PrefixEnd(AssetEntry[] entries, string prefix)
        => LowerBound(entries, prefix + '\uffff');

    private sealed class ResourceAccumulator
    {
        private readonly string _logicalPath;
        private string _type;
        private int _typePriority;
        private int _count;

        public ResourceAccumulator(AssetEntry entry, string logicalPath)
        {
            _logicalPath = logicalPath;
            var type = DescribeType(entry.Extension);
            _type = type.Type;
            _typePriority = type.Priority;
            _count = 1;
        }

        public void Add(AssetEntry entry)
        {
            _count++;
            var type = DescribeType(entry.Extension);
            if (type.Priority <= _typePriority) return;
            _type = type.Type;
            _typePriority = type.Priority;
        }

        public AssetBrowserEntry ToBrowserEntry()
        {
            var name = Path.GetFileName(_logicalPath);
            if (string.IsNullOrEmpty(name)) name = _logicalPath;
            if (!PackageExtensions.Contains(Path.GetExtension(_logicalPath).TrimStart('.')) &&
                _typePriority == 0)
            {
                name = Path.GetFileNameWithoutExtension(_logicalPath);
            }

            return new AssetBrowserEntry(name, _logicalPath, _type, false, _count);
        }

        private static (string Type, int Priority) DescribeType(string extension)
        {
            if (extension.Equals("umap", StringComparison.OrdinalIgnoreCase)) return ("地图", 30);
            if (extension.Equals("uasset", StringComparison.OrdinalIgnoreCase)) return ("资源包", 20);
            if (extension.Equals("uexp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals("ubulk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals("uptnl", StringComparison.OrdinalIgnoreCase))
            {
                return ("资源包数据", 10);
            }

            return (string.IsNullOrWhiteSpace(extension) ? "文件" : extension.ToUpperInvariant(), 0);
        }
    }
}
