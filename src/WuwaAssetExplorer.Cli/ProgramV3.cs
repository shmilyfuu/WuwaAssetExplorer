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
        return await CommandRunner.RunAsync(session, options.Command);

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
    private static int DefaultIndexJobs => Math.Clamp(Environment.ProcessorCount / 2, 4, 12);

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
                    Require(tokens, 2, "imports <资源名或路径> [--assets]");
                    PrintList(HasFlag(tokens, "--assets") ? session.GetAssetImports(tokens[1]) : session.GetImports(tokens[1]));
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
                    Require(tokens, 2, "backrefs <资源名或路径> [--path 路径过滤] [--max 200] [--jobs N] [--scan]");
                    var backMax = ReadIntOption(tokens, "--max", 200);
                    var backJobs = Math.Clamp(ReadIntOption(tokens, "--jobs", DefaultIndexJobs), 1, 12);
                    var backPath = ReadStringOption(tokens, "--path");
                    var directScan = HasFlag(tokens, "--scan");
                    var semantic = await session.FindBackReferencesAsync(tokens[1], backPath, backJobs, directScan);
                    Console.WriteLine();
                    Console.WriteLine($"语义反向引用包：{semantic.Count:N0} 个");
                    PrintList(semantic.Take(backMax));
                    if (semantic.Count > backMax) Console.WriteLine($"…还有 {semantic.Count - backMax:N0} 个结果，使用 --max 调大显示数量。");
                    return 0;

                case "index":
                    return await RunIndexCommandAsync(session, tokens);

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

    private static async Task<int> RunIndexCommandAsync(ProbeSession session, IReadOnlyList<string> tokens)
    {
        var action = tokens.Count >= 2 ? tokens[1].ToLowerInvariant() : "status";
        var jobs = Math.Clamp(ReadIntOption(tokens, "--jobs", DefaultIndexJobs), 1, 12);

        switch (action)
        {
            case "status":
                PrintIndexStatus(session.GetReferenceIndexStatus());
                return 0;

            case "build":
                PrintBuildResult(await session.BuildReferenceIndexAsync(jobs, rebuild: false, retryFailed: false));
                return 0;

            case "rebuild":
                PrintBuildResult(await session.BuildReferenceIndexAsync(jobs, rebuild: true, retryFailed: false));
                return 0;

            case "retry":
            case "retry-failed":
                PrintBuildResult(await session.BuildReferenceIndexAsync(jobs, rebuild: false, retryFailed: true));
                return 0;

            case "clear":
                session.ClearReferenceIndex();
                Console.WriteLine("反向引用索引已删除。下一次 backrefs 会重新建立。");
                return 0;

            default:
                throw new ArgumentException("用法：index [status|build|rebuild|retry|clear] [--jobs N]");
        }
    }

    private static void PrintIndexStatus(ReferenceIndexStatus status)
    {
        Console.WriteLine($"索引文件：{status.DatabasePath}");
        if (!status.Exists)
        {
            Console.WriteLine("状态：尚未建立");
            return;
        }

        Console.WriteLine($"状态：{(status.Ready ? "可用" : status.FingerprintMatches ? "未完成，可续建" : "资源版本已变化，需要重建")}");
        Console.WriteLine($"已索引包：{status.IndexedPackages:N0}");
        Console.WriteLine($"解析失败包：{status.FailedPackages:N0}");
        Console.WriteLine($"硬引用边：{status.EdgeCount:N0}");
    }

    private static void PrintBuildResult(ReferenceIndexBuildResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"索引完成：总包 {result.TotalPackages:N0}，此前已有 {result.PreviouslyIndexedPackages:N0}，本次处理 {result.ScannedPackages:N0}");
        Console.WriteLine($"解析失败包：{result.FailedPackages:N0}");
        Console.WriteLine($"硬引用边：{result.EdgeCount:N0}");
        Console.WriteLine($"本次耗时：{result.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"索引文件：{result.DatabasePath}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
命令：
  search <文本> [--max N]
      搜索已挂载资源目录。

  where <资源名或路径>
      定位资源实际路径。

  imports <资源名或路径> [--assets]
      读取该包的 ImportMap。
      --assets 仅显示标准化后的资源包依赖，便于排除 /Script 类型信息和重复对象路径。

  refs <资源名或路径>
      解析导出对象并提取正向路径引用。

  backrefs <资源名或路径> [--path 文本] [--max N] [--jobs N] [--scan]
      默认查询持久化 SQLite 反向引用索引。
      首次使用会自动建立全局 ImportMap 索引；之后同版本客户端直接查询索引。
      索引建立支持续建。--scan 可绕过索引执行一次直接扫描，用于诊断对比。
      匹配使用完整标准化包路径，不再用短资源名作为全局命中条件。

  index status
      查看索引状态、包数量、失败数量和硬引用边数量。

  index build [--jobs N]
      建立或继续未完成的索引。

  index rebuild [--jobs N]
      删除旧索引并从头建立。

  index retry [--jobs N]
      重新尝试此前解析失败的包，其余已完成包保持缓存。

  index clear
      删除本地反向引用索引。

  grep <字符串> [--path 文本] [--max N] [--jobs N]
      原始 UTF-8/UTF-16 字符串扫描，只用于补充排查软引用或名称线索。

  dump <资源名或路径>
      将导出对象保存为 JSON 到 Output 目录。

  exit
      退出。

推荐先执行：
  index build

之后可连续追踪：
  backrefs T_Aimisi_Sub_40001
  backrefs T_Aimisi_Sub_40002
  backrefs T_AMS_atlas_12001
  imports MI_Aimisi_Sub_40001_S --assets

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

    private static bool HasFlag(IReadOnlyList<string> tokens, string name)
        => tokens.Any(token => token.Equals(name, StringComparison.OrdinalIgnoreCase));

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
    private readonly ReferenceIndex _referenceIndex;
    private readonly string _containerFingerprint;

    private ProbeSession(DefaultFileProvider provider, string paksPath)
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

        _referenceIndex = new ReferenceIndex(Path.Combine(AppContext.BaseDirectory, "Data", "reference-index-v1.sqlite"));
        _containerFingerprint = ReferenceIndex.ComputeContainerFingerprint(paksPath);
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
            return new ProbeSession(provider, paksPath);
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

    public IReadOnlyList<string> GetAssetImports(string query)
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
                var canonical = AssetPathNormalizer.CanonicalFromResolvedObjectPath(resolved.GetPathName());
                if (!string.IsNullOrWhiteSpace(canonical)) results.Add(canonical);
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

    public ReferenceIndexStatus GetReferenceIndexStatus()
        => _referenceIndex.GetStatus(_containerFingerprint);

    public void ClearReferenceIndex()
        => _referenceIndex.Clear();

    public async Task<ReferenceIndexBuildResult> BuildReferenceIndexAsync(int jobs, bool rebuild, bool retryFailed)
    {
        var packages = _provider.Files
            .Where(kvp => kvp.Value.IsUePackage && IsPackagePath(kvp.Key))
            .Select(kvp => kvp.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await _referenceIndex.BuildAsync(
            _provider,
            packages,
            _containerFingerprint,
            jobs,
            rebuild,
            retryFailed,
            AssetPathNormalizer.CanonicalFromResolvedObjectPath);
    }

    public async Task<IReadOnlyList<string>> FindBackReferencesAsync(string query, string? pathFilter, int jobs, bool directScan)
    {
        if (directScan)
            return await FindSemanticBackReferencesDirectAsync(query, pathFilter, jobs);

        var targetPackage = ResolveSinglePackage(query);
        var targetCanonical = AssetPathNormalizer.CanonicalFromPackageFile(targetPackage);
        var status = GetReferenceIndexStatus();

        if (!status.Ready)
        {
            if (!status.Exists)
                Console.WriteLine("尚未建立反向引用索引，首次 backrefs 将建立一次全局索引；后续查询直接复用。");
            else if (!status.FingerprintMatches)
                Console.WriteLine("检测到客户端资源版本变化，反向引用索引需要更新。");
            else
                Console.WriteLine("检测到未完成的反向引用索引，将从上次进度继续。");

            await BuildReferenceIndexAsync(jobs, rebuild: false, retryFailed: false);
            status = GetReferenceIndexStatus();
        }

        if (!status.Ready)
            throw new InvalidOperationException("反向引用索引仍未达到可查询状态，请执行 index status 检查。");

        Console.WriteLine($"索引查询：{targetCanonical}");
        if (!string.IsNullOrWhiteSpace(pathFilter)) Console.WriteLine($"路径过滤：{pathFilter}");
        if (status.FailedPackages > 0) Console.WriteLine($"提示：索引中有 {status.FailedPackages:N0} 个包解析失败，可用 index retry 再尝试。");
        return _referenceIndex.QueryBackReferences(targetCanonical, pathFilter);
    }

    private async Task<IReadOnlyList<string>> FindSemanticBackReferencesDirectAsync(string query, string? pathFilter, int jobs)
    {
        var targetPackage = ResolveSinglePackage(query);
        var targetCanonical = AssetPathNormalizer.CanonicalFromPackageFile(targetPackage);
        var candidates = _provider.Files
            .Where(kvp => kvp.Value.IsUePackage && IsPackagePath(kvp.Key) &&
                          !kvp.Key.Equals(targetPackage, StringComparison.OrdinalIgnoreCase) &&
                          (string.IsNullOrWhiteSpace(pathFilter) || kvp.Key.Contains(pathFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(kvp => kvp.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"直接扫描 {candidates.Length:N0} 个 UE 包的 ImportMap，目标：{targetCanonical}");
        if (!string.IsNullOrWhiteSpace(pathFilter)) Console.WriteLine($"路径过滤：{pathFilter}");

        var results = new ConcurrentBag<string>();
        var processed = 0;
        var failed = 0;
        var sw = Stopwatch.StartNew();
        long lastProgress = 0;

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

                    var canonical = AssetPathNormalizer.CanonicalFromResolvedObjectPath(resolved.GetPathName());
                    if (canonical is not null && canonical.Equals(targetCanonical, StringComparison.OrdinalIgnoreCase))
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
        long lastProgress = 0;

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
        var matches = ResolveMatches(query, 50).Where(IsPackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length == 0) throw new FileNotFoundException($"没有定位到资源：{query}");
        throw new InvalidOperationException("资源名不唯一，请使用完整路径：" + Environment.NewLine + string.Join(Environment.NewLine, matches.Select(x => "  " + x)));
    }

    private static bool IsPackagePath(string p)
        => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

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