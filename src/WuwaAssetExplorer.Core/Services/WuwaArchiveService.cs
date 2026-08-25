using System.Diagnostics;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using WuwaAssetExplorer.Core.Models;

namespace WuwaAssetExplorer.Core.Services;

public sealed class WuwaArchiveService
{
    private readonly AssetCatalog _catalog = new();
    private DefaultFileProvider? _provider;

    public AssetCatalog Catalog => _catalog;
    public bool IsLoaded => _provider is not null && _catalog.Count > 0;

    public async Task<ArchiveLoadResult> LoadAsync(
        string paksPath,
        AesKeySet aesKeys,
        IProgress<ArchiveLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = WuwaPathResolver.ResolvePaksDirectory(paksPath);
        var stopwatch = Stopwatch.StartNew();

        progress?.Report(new ArchiveLoadProgress(
            ArchiveLoadStage.ScanningArchives,
            0,
            0,
            0,
            TimeSpan.Zero));

        var result = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versions = new VersionContainer(EGame.GAME_WutheringWaves);
            var provider = new DefaultFileProvider(
                new DirectoryInfo(resolved),
                SearchOption.AllDirectories,
                versions,
                StringComparer.OrdinalIgnoreCase)
            {
                ReadScriptData = true,
                ReadShaderMaps = false,
                ReadNaniteData = false,
                UseLazyPackageSerialization = true
            };

            provider.Initialize();
            cancellationToken.ThrowIfCancellationRequested();

            var totalArchives = provider.UnloadedVfs.Count + provider.MountedVfs.Count;
            var mountedArchives = provider.MountedVfs.Count;
            var mountStopwatch = Stopwatch.StartNew();

            progress?.Report(new ArchiveLoadProgress(
                ArchiveLoadStage.MountingArchives,
                mountedArchives,
                totalArchives,
                provider.Files.Count,
                mountStopwatch.Elapsed));

            EventHandler<int> onVfsMounted = (_, indexedFiles) =>
            {
                var mounted = Interlocked.Increment(ref mountedArchives);
                progress?.Report(new ArchiveLoadProgress(
                    ArchiveLoadStage.MountingArchives,
                    mounted,
                    totalArchives,
                    indexedFiles,
                    mountStopwatch.Elapsed));
            };

            provider.VfsMounted += onVfsMounted;
            try
            {
                var keys = BuildKeys(aesKeys);
                provider.SubmitKeys(keys);
                provider.PostMount();
                cancellationToken.ThrowIfCancellationRequested();

                var paths = provider.Files.Keys.ToArray();
                progress?.Report(new ArchiveLoadProgress(
                    ArchiveLoadStage.BuildingCatalog,
                    mountedArchives,
                    totalArchives,
                    paths.Length,
                    mountStopwatch.Elapsed));

                _catalog.Replace(paths);
                _provider = provider;

                progress?.Report(new ArchiveLoadProgress(
                    ArchiveLoadStage.Completed,
                    mountedArchives,
                    totalArchives,
                    paths.Length,
                    mountStopwatch.Elapsed));

                return paths.Length;
            }
            finally
            {
                provider.VfsMounted -= onVfsMounted;
            }
        }, cancellationToken);

        stopwatch.Stop();
        return new ArchiveLoadResult(result, stopwatch.Elapsed);
    }

    public AssetDirectorySnapshot Browse(string? directoryPath)
        => _catalog.Browse(directoryPath);

    public IReadOnlyList<AssetBrowserEntry> Search(string query, int maxResults = 400)
        => _catalog.Search(query, maxResults);

    private static IReadOnlyList<KeyValuePair<FGuid, FAesKey>> BuildKeys(AesKeySet source)
    {
        var keys = new List<KeyValuePair<FGuid, FAesKey>>(source.DynamicKeys.Count + 1)
        {
            new(new FGuid(0), new FAesKey(source.MainKey))
        };

        foreach (var dynamicKey in source.DynamicKeys)
        {
            keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(dynamicKey.Guid), new FAesKey(dynamicKey.Key)));
        }

        return keys;
    }
}
