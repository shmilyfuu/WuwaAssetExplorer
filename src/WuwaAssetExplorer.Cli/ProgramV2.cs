using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using WuwaAssetExplorer.Core.Models;
using WuwaAssetExplorer.Core.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    var options = CliOptions.Parse(args);
    var settings = CliSettings.Load();
    var paksPath = options.PaksPath ?? settings.PaksPath;

    if (string.IsNullOrWhiteSpace(paksPath))
    {
        Console.Write("请输入鸣潮游戏目录或 Client\\Content\\Paks 路径：");
        paksPath = Console.ReadLine()?.Trim().Trim('"');
    }

    if (string.IsNullOrWhiteSpace(paksPath))
    {
        Console.Error.WriteLine("未提供游戏目录。");
        return 2;
    }

    var resolvedPaks = WuwaPathResolver.ResolvePaksDirectory(paksPath);
    var endpoint = options.Endpoint ?? settings.Endpoint ?? AesEndpointService.DefaultEndpoint;

    Console.WriteLine($"Paks: {resolvedPaks}");
    Console.WriteLine("正在获取 AES…");
    var aesResult = await new AesEndpointService().GetKeysAsync(endpoint);
    Console.WriteLine($"AES: 主密钥 + {aesResult.Keys.DynamicKeys.Count} 个动态密钥{(aesResult.UsedCache ? "（缓存）" : string.Empty)}");

    var session = await ProbeSession.OpenAsync(resolvedPaks, aesResult.Keys);
    CliSettings.Save(new CliSettings(resolvedPaks, endpoint));

    Console.WriteLine($"已挂载 {session.AssetCount:N0} 个资源。输入 help 查看命令。\n");

    if (options.Command.Count > 0)
    {
        return await CommandRunner.RunAsync(session, options.Command);
    }

    while (true)
    {
        Console.Write("wuwa> ");
        var line = Console.ReadLine();
        if (line is null) break;
        var tokens = Tokenize(line);
        if (tokens.Count == 0) continue;
        if (tokens[0].Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            tokens[0].Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

        await CommandRunner.RunAsync(session, tokens);
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("错误：" + ex.Message);
    if (Environment.GetEnvironmentVariable("WUWA_PROBE_DEBUG") == "1") Console.Error.WriteLine(ex);
    return 1;
}

static List<string> Tokenize(string commandLine)
{
    var tokens = new List<string>();
    var current = new StringBuilder();
    var inQuotes = false;
    foreach (var c in commandLine)
    {
        if (c == '"') { inQuotes = !inQuotes; continue; }
        if (char.IsWhiteSpace(c) && !inQuotes)
        {
            if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            continue;
        }
        current.Append(c);
    }
    if (current.Length > 0) tokens.Add(current.ToString());
    return tokens;
}

internal static class CommandRunner
{
    public static async Task<int> RunAsync(ProbeSession session, IReadOnlyList<string> tokens)
    {
        try
        {
            switch (tokens[0].ToLowerInvariant())
            {
                case "help":
                case "?":
                    PrintHelp();
                    return 0;

                case "search":
                    Require(tokens, 2, "search <名称或路径片段> [--max 100]");
                    PrintList(session.Search(tokens[1], ReadIntOption(tokens, "--max", 100)));
                    return 0;

                case "where":
                    Require(tokens, 2, "where <资源名或路径>");
                    PrintList(session.ResolveMatches(tokens[1], 50));
                    return 0;

                case "imports":
                    Require(tokens, 2, "imports <资源名或路径>");
                    PrintList(session.GetImports(tokens[1]));
                    return 0;

                case "refs":
                    Require(tokens, 2, "refs <资源名或路径>");
                    var refs = session.GetForwardReferences(tokens[1]);
                    if (refs.Count == 0) Console.WriteLine("未从已解析导出对象中提取到路径引用。可再用 imports 或 dump 检查。");
                    else PrintList(refs);
                    return 0;

                case "dump":
                    Require(tokens, 2, "dump <资源名或路径>");
                    Console.WriteLine(session.DumpJson(tokens[1]));
                    return 0;

                case "backrefs":
                    Require(tokens, 2, "backrefs <资源名或路径> [--path 路径过滤] [--max 200] [--jobs 4]");
                    var backMax = ReadIntOption(tokens, "--max", 200);
                    var backJobs = Math.Clamp(ReadIntOption(tokens, "--jobs", 4), 1, 12);
                    var backPath = ReadStringOption(tokens, "--path");
                    var semantic = await session.FindSemanticBackReferencesAsync(tokens[1], backPath, backJobs);
                    Console.WriteLine();
                    Console.WriteLine($"语义反向引用包：{semantic.Count:N0} 个");
                    PrintList(semantic.Take(backMax));
                    if (semantic.Count > backMax) Console.WriteLine($"…还有 {semantic.Count - backMax:N0} 个结果，使用 --max 调大显示数量。");
                    return 0;

                case "grep":
                case "rawgrep":
                    Require(tokens, 2, "grep <字符串> [--path 路径过滤] [--max 200] [--jobs 4]");
                    var rawMax = ReadIntOption(tokens, "--max", 200);
                    var rawJobs = Math.Clamp(ReadIntOption(tokens, "--jobs", 4), 1, 16);
                    var rawPath = ReadStringOption(tokens, "--path");
                    var raw = await session.FindRawStringMatchesAsync(tokens[1], rawPath, rawJobs);
                    Console.WriteLine();
                    Console.WriteLine($"原始字符串命中包：{raw.Count:N0} 个");
                    PrintList(raw.Take(rawMax));
                    if (raw.Count > rawMax) Console.WriteLine($"…还有 {raw.Count - rawMax:N0} 个结果，使用 --max 调大显示数量。");
                    return 0;

                default:
                    Console.WriteLine($"未知命令：{tokens[0]}。输入 help 查看命令。");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("命令失败：" + ex.Message);
            if (Environment.GetEnvironmentVariable("WUWA_PROBE_DEBUG") == "1") Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
命令：
  search <文本> [--max N]
      搜索已挂载资源目录。

  where <资源名或路径>
      定位资源实际路径。

  imports <资源名或路径>
      读取该包的 ImportMap，列出 UE 硬引用。速度快，适合验证材质 -> 贴图等关系。

  refs <资源名或路径>
      解析导出对象并提取正向路径引用。

  backrefs <资源名或路径> [--path 文本] [--max N] [--jobs N]
      逐包读取 ImportMap，并通过 FPackageIndex/ResolvedObject 查询真正的 UE 硬引用。
      会排除目标资源本体。该命令比原始字符串扫描更可信，但软引用仍可能需要 refs/dump 检查。

  grep <字符串> [--path 文本] [--max N] [--jobs N]
      原始 UTF-8/UTF-16 字符串扫描。仅用于补充排查，不代表真实引用关系。

  dump <资源名或路径>
      将导出对象保存为 JSON 到 Output 目录。

  exit
      退出。

爱弥斯当前建议：
  imports MI_Trans_Sub_21004_S
  imports MI_Trans_Sub_21007_S
  imports MI_Trans_Sub_21008_S
  imports MI_Trans_Sub_21009_S
  imports MI_Trans_Sub_21012_S
  backrefs T_Aimisi_Sub_40001 --path Aki/Effect

首次成功启动后会记住 Paks 路径；AES Endpoint 默认使用 wuwa-keys。
""");
    }

    private static void Require(IReadOnlyList<string> tokens, int count, string usage)
    {
        if (tokens.Count < count) throw new ArgumentException("用法：" + usage);
    }

    private static int ReadIntOption(IReadOnlyList<string> tokens, string name, int fallback)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
            if (tokens[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[i + 1], out var value)) return value;
        return fallback;
    }

    private static string? ReadStringOption(IReadOnlyList<string> tokens, string name)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
            if (tokens[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return tokens[i + 1];
        return null;
    }

    private static void PrintList(IEnumerable<string> values)
    {
        var any = false;
        foreach (var value in values) { any = true; Console.WriteLine(value); }
        if (!any) Console.WriteLine("无结果。");
    }
}

internal sealed class ProbeSession
{
    private static readonly Regex AssetPathRegex = new(@"(?:/Game/|Client/Content/|Engine/Content/)[A-Za-z0-9_./\\-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly DefaultFileProvider _provider;
    private readonly string[] _paths;
    private readonly Dictionary<string, List<string>> _byShortName;

    private ProbeSession(DefaultFileProvider provider)
    {
        _provider = provider;
        _paths = provider.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        _byShortName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _paths)
        {
            var name = Path.GetFileNameWithoutExtension(path.Replace('/', Path.DirectorySeparatorChar));
            if (!_byShortName.TryGetValue(name, out var list)) { list = []; _byShortName[name] = list; }
            list.Add(path);
        }
    }

    public int AssetCount => _paths.Length;

    public static async Task<ProbeSession> OpenAsync(string paksPath, AesKeySet aesKeys)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var provider = new DefaultFileProvider(new DirectoryInfo(paksPath), SearchOption.AllDirectories, new VersionContainer(EGame.GAME_WutheringWaves), StringComparer.OrdinalIgnoreCase)
            {
                ReadScriptData = true,
                ReadShaderMaps = false,
                ReadNaniteData = false,
                UseLazyPackageSerialization = true
            };
            Console.WriteLine("正在扫描容器…");
            provider.Initialize();
            Console.WriteLine($"发现容器：{provider.UnloadedVfs.Count + provider.MountedVfs.Count:N0}");
            provider.SubmitKeys(BuildKeys(aesKeys));
            Console.WriteLine("正在解密并挂载目录索引…");
            provider.PostMount();
            sw.Stop();
            Console.WriteLine($"挂载完成：{provider.Files.Count:N0} 个文件，{sw.Elapsed.TotalSeconds:F1}s");
            return new ProbeSession(provider);
        });
    }

    public IReadOnlyList<string> Search(string query, int maxResults)
        => _paths.Where(p => p.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(Math.Max(1, maxResults)).ToArray();

    public IReadOnlyList<string> ResolveMatches(string query, int maxResults)
    {
        var normalized = query.Trim().Trim('"').Replace('\\', '/');
        if (_provider.Files.ContainsKey(normalized)) return [normalized];
        var shortName = Path.GetFileNameWithoutExtension(normalized);
        if (_byShortName.TryGetValue(shortName, out var exact)) return exact.Take(maxResults).ToArray();
        return Search(normalized, maxResults);
    }

    public IReadOnlyList<string> GetImports(string query)
    {
        var path = ResolveSinglePackage(query);
        var package = _provider.LoadPackage(path);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < package.ImportMapLength; i++)
        {
            try
            {
                var resolved = package.ResolvePackageIndex(new FPackageIndex(package, -(i + 1)));
                if (resolved is null) continue;
                var value = resolved.GetPathName();
                if (!string.IsNullOrWhiteSpace(value)) results.Add(value);
            }
            catch { }
        }
        return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> GetForwardReferences(string query)
    {
        var path = ResolveSinglePackage(query);
        var package = _provider.LoadPackage(path);
        var results = new HashSet<string>(GetImports(query), StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = JsonConvert.SerializeObject(package.GetExports(), Formatting.None);
            foreach (Match m in AssetPathRegex.Matches(json)) results.Add(m.Value.TrimEnd('.', ','));
        }
        catch { }
        return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string DumpJson(string query)
    {
        var path = ResolveSinglePackage(query);
        var json = JsonConvert.SerializeObject(_provider.LoadPackage(path).GetExports(), Formatting.Indented);
        var outputDir = Path.Combine(AppContext.BaseDirectory, "Output");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + ".json");
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        return outputPath;
    }

    public async Task<IReadOnlyList<string>> FindSemanticBackReferencesAsync(string query, string? pathFilter, int jobs)
    {
        var targets = ResolveMatches(query, 50)
            .Where(IsPackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0) throw new FileNotFoundException($"没有定位到目标资源：{query}");

        var targetNames = targets.Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetCanonical = targets.Select(ToCanonicalPackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPackages = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = _provider.Files
            .Where(kvp => kvp.Value.IsUePackage && IsPackagePath(kvp.Key) && !targetPackages.Contains(kvp.Key) &&
                          (string.IsNullOrWhiteSpace(pathFilter) || kvp.Key.Contains(pathFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(kvp => kvp.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"扫描 {candidates.Length:N0} 个 UE 包的 ImportMap，目标：{string.Join(", ", targetNames)}");
        if (!string.IsNullOrWhiteSpace(pathFilter)) Console.WriteLine($"路径过滤：{pathFilter}");

        var results = new ConcurrentBag<string>();
        var processed = 0;
        var failed = 0;
        var sw = Stopwatch.StartNew();
        var lastProgress = 0L;

        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = jobs }, async (path, _) =>
        {
            try
            {
                var package = await _provider.LoadPackageAsync(path).ConfigureAwait(false);
                for (var i = 0; i < package.ImportMapLength; i++)
                {
                    ResolvedObject? resolved = null;
                    try { resolved = package.ResolvePackageIndex(new FPackageIndex(package, -(i + 1))); } catch { }
                    if (resolved is null) continue;

                    var name = resolved.Name.ToString();
                    var resolvedPath = NormalizeResolvedPath(resolved.GetPathName());
                    if (targetNames.Contains(name) || targetCanonical.Any(t => PathMatchesTarget(resolvedPath, t)))
                    {
                        results.Add(path);
                        break;
                    }
                }
            }
            catch { Interlocked.Increment(ref failed); }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                ReportProgress(done, candidates.Length, results.Count, sw, ref lastProgress);
            }
        });

        Console.WriteLine();
        if (failed > 0) Console.WriteLine($"跳过无法解析的包：{failed:N0}");
        Console.WriteLine($"扫描耗时：{sw.Elapsed.TotalSeconds:F1}s");
        return results.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<string>> FindRawStringMatchesAsync(string needle, string? pathFilter, int jobs)
    {
        needle = needle.Trim();
        if (needle.Length == 0) throw new ArgumentException("搜索字符串不能为空。");
        var ascii = Encoding.UTF8.GetBytes(needle);
        var utf16 = Encoding.Unicode.GetBytes(needle);
        var selfPackages = ResolveMatches(needle, 50).Where(IsPackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _provider.Files
            .Where(kvp => kvp.Value.IsUePackage && !selfPackages.Contains(kvp.Key) &&
                          (string.IsNullOrWhiteSpace(pathFilter) || kvp.Key.Contains(pathFilter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Console.WriteLine($"原始字符串扫描 {candidates.Length:N0} 个 UE 包：{needle}");
        var results = new ConcurrentBag<string>();
        var processed = 0;
        var failed = 0;
        var sw = Stopwatch.StartNew();
        var lastProgress = 0L;

        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = jobs }, async (kvp, _) =>
        {
            try
            {
                var data = await kvp.Value.ReadAsync().ConfigureAwait(false);
                if (data.AsSpan().IndexOf(ascii) >= 0 || data.AsSpan().IndexOf(utf16) >= 0) results.Add(kvp.Key);
            }
            catch { Interlocked.Increment(ref failed); }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                ReportProgress(done, candidates.Length, results.Count, sw, ref lastProgress);
            }
        });

        Console.WriteLine();
        if (failed > 0) Console.WriteLine($"跳过读取失败资源：{failed:N0}");
        Console.WriteLine($"扫描耗时：{sw.Elapsed.TotalSeconds:F1}s");
        return results.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string ResolveSinglePackage(string query)
    {
        var matches = ResolveMatches(query, 20).Where(IsPackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length == 0) throw new FileNotFoundException($"没有定位到资源：{query}");
        throw new InvalidOperationException("资源名不唯一，请使用完整路径：" + Environment.NewLine + string.Join(Environment.NewLine, matches.Select(x => "  " + x)));
    }

    private static bool IsPackagePath(string p)
        => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    private static string ToCanonicalPackagePath(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("Client/Content/", StringComparison.OrdinalIgnoreCase)) p = "/Game/" + p["Client/Content/".Length..];
        else if (p.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase)) p = "/Engine/" + p["Engine/Content/".Length..];
        var ext = Path.GetExtension(p);
        if (!string.IsNullOrEmpty(ext)) p = p[..^ext.Length];
        return p.TrimEnd('/');
    }

    private static string NormalizeResolvedPath(string path)
        => path.Replace('\\', '/').Trim('"', '\'', ' ');

    private static bool PathMatchesTarget(string resolvedPath, string targetPackage)
    {
        if (resolvedPath.Equals(targetPackage, StringComparison.OrdinalIgnoreCase)) return true;
        if (resolvedPath.StartsWith(targetPackage + ".", StringComparison.OrdinalIgnoreCase)) return true;
        if (resolvedPath.StartsWith(targetPackage + ":", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void ReportProgress(int done, int total, int hits, Stopwatch sw, ref long lastProgress)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastProgress);
        if ((done == total || now - previous >= 1000) && Interlocked.CompareExchange(ref lastProgress, now, previous) == previous)
        {
            var rate = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var remaining = total - done;
            var eta = rate > 0 ? TimeSpan.FromSeconds(remaining / rate) : TimeSpan.Zero;
            Console.Write($"\r{done,8:N0}/{total:N0}  命中 {hits,5:N0}  {rate,7:N0}/s  ETA {eta:hh\\:mm\\:ss}   ");
        }
    }

    private static IReadOnlyList<KeyValuePair<FGuid, FAesKey>> BuildKeys(AesKeySet source)
    {
        var keys = new List<KeyValuePair<FGuid, FAesKey>>(source.DynamicKeys.Count + 1)
        {
            new(new FGuid(0), new FAesKey(source.MainKey))
        };
        foreach (var dynamicKey in source.DynamicKeys)
            keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(dynamicKey.Guid), new FAesKey(dynamicKey.Key)));
        return keys;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

internal sealed record CliOptions(string? PaksPath, string? Endpoint, IReadOnlyList<string> Command)
{
    public static CliOptions Parse(string[] args)
    {
        string? paks = null;
        string? endpoint = null;
        var command = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--paks", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) paks = args[++i];
            else if (args[i].Equals("--endpoint", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) endpoint = args[++i];
            else command.Add(args[i]);
        }
        return new CliOptions(paks, endpoint, command);
    }
}

internal sealed record CliSettings(string? PaksPath, string? Endpoint)
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Data", "cli-settings.json");

    public static CliSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CliSettings(null, null);
            return System.Text.Json.JsonSerializer.Deserialize<CliSettings>(File.ReadAllText(FilePath)) ?? new CliSettings(null, null);
        }
        catch { return new CliSettings(null, null); }
    }

    public static void Save(CliSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
