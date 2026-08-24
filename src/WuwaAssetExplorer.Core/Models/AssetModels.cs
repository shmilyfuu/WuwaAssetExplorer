namespace WuwaAssetExplorer.Core.Models;

public sealed record AssetEntry(string Name, string FullPath, string Extension)
{
    public static AssetEntry FromPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(fileName).TrimStart('.');
        var name = Path.GetFileNameWithoutExtension(fileName);
        return new AssetEntry(name, normalized, extension);
    }
}

public sealed record AssetBrowserEntry(
    string Name,
    string FullPath,
    string Type,
    bool IsDirectory,
    int PhysicalFileCount)
{
    public string Detail => IsDirectory
        ? "文件夹"
        : PhysicalFileCount > 1
            ? $"{Type} · {PhysicalFileCount:N0} 个组成文件"
            : Type;

    public static AssetBrowserEntry Directory(string name, string fullPath)
        => new(name, fullPath, "文件夹", true, 0);
}

public sealed record AssetDirectorySnapshot(
    string DirectoryPath,
    IReadOnlyList<AssetBrowserEntry> Items,
    int DirectoryCount,
    int ResourceCount);

public sealed record DynamicAesKey(string Guid, string Key);

public sealed record AesKeySet(string MainKey, IReadOnlyList<DynamicAesKey> DynamicKeys);

public sealed record AesFetchResult(AesKeySet Keys, bool UsedCache, DateTimeOffset RetrievedAt);

public enum ArchiveLoadStage
{
    ScanningArchives,
    MountingArchives,
    BuildingCatalog,
    Completed
}

public sealed record ArchiveLoadProgress(
    ArchiveLoadStage Stage,
    int MountedArchives,
    int TotalArchives,
    int IndexedFiles,
    TimeSpan StageElapsed);

public sealed record ArchiveLoadResult(int AssetCount, TimeSpan Elapsed);
