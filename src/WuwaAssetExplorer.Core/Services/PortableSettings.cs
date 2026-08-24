using System.Text.Json;

namespace WuwaAssetExplorer.Core.Services;

public sealed record AppSettings(string? PaksPath, string AesEndpoint)
{
    public static AppSettings Default { get; } = new(null, AesEndpointService.DefaultEndpoint);
}

public static class PortablePaths
{
    public const string RootEnvironmentVariable = "WUWA_ASSET_EXPLORER_ROOT";

    public static string ApplicationRootDirectory => ResolveApplicationRootDirectory();
    public static string DataDirectory => Path.Combine(ApplicationRootDirectory, "Data");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string AesCacheFile => Path.Combine(DataDirectory, "aes-cache.json");
    public static string StartupLogFile => Path.Combine(DataDirectory, "startup.log");
    public static string ReferenceIndexFile => Path.Combine(DataDirectory, "index.db");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        var probe = Path.Combine(DataDirectory, ".write-test");
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
    }

    private static string ResolveApplicationRootDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot) && Path.IsPathFullyQualified(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var baseDirectory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        if (baseDirectory.Name.Equals("Runtime", StringComparison.OrdinalIgnoreCase) && baseDirectory.Parent is not null)
        {
            return baseDirectory.Parent.FullName;
        }

        return baseDirectory.FullName;
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        PortablePaths.EnsureDataDirectory();
        if (!File.Exists(PortablePaths.SettingsFile)) return AppSettings.Default;

        await using var stream = File.OpenRead(PortablePaths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
               ?? AppSettings.Default;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        PortablePaths.EnsureDataDirectory();
        await using var stream = File.Create(PortablePaths.SettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}

public static class WuwaPathResolver
{
    public static string ResolvePaksDirectory(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) throw new ArgumentException("游戏目录不能为空。", nameof(selectedPath));
        var root = Path.GetFullPath(selectedPath);

        var candidates = new[]
        {
            root,
            Path.Combine(root, "Client", "Content", "Paks"),
            Path.Combine(root, "Wuthering Waves Game", "Client", "Content", "Paks")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(candidate)) continue;
            if (Directory.EnumerateFiles(candidate, "*.pak", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(candidate, "*.utoc", SearchOption.TopDirectoryOnly).Any())
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("未找到鸣潮 Client\\Content\\Paks。请选择游戏目录或 Paks 目录。");
    }
}
