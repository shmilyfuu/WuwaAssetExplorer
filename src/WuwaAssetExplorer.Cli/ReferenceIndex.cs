using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Data.Sqlite;

internal sealed record ReferenceIndexStatus(
    string DatabasePath,
    bool Exists,
    bool FingerprintMatches,
    bool Complete,
    int IndexedPackages,
    int FailedPackages,
    long EdgeCount,
    string? StoredFingerprint)
{
    public bool Ready => Exists && FingerprintMatches && Complete;
}

internal sealed record ReferenceIndexBuildResult(
    int TotalPackages,
    int PreviouslyIndexedPackages,
    int ScannedPackages,
    int FailedPackages,
    long EdgeCount,
    TimeSpan Elapsed,
    string DatabasePath);

internal sealed class ReferenceIndex
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;

    public ReferenceIndex(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    public static string ComputeContainerFingerprint(string paksPath)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pak", ".utoc", ".ucas", ".sig"
        };

        var files = Directory.EnumerateFiles(paksPath, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(paksPath, path).Replace('\\', '/');
            var line = $"{relative}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public ReferenceIndexStatus GetStatus(string currentFingerprint)
    {
        if (!File.Exists(_databasePath))
            return new ReferenceIndexStatus(_databasePath, false, false, false, 0, 0, 0, null);

        try
        {
            using var connection = OpenConnection(createIfMissing: false);
            EnsureSchema(connection);

            var storedSchema = ReadMetadata(connection, "schema_version");
            var storedFingerprint = ReadMetadata(connection, "container_fingerprint");
            var complete = string.Equals(ReadMetadata(connection, "complete"), "1", StringComparison.Ordinal);
            var schemaMatches = string.Equals(storedSchema, SchemaVersion.ToString(), StringComparison.Ordinal);
            var fingerprintMatches = schemaMatches && string.Equals(storedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
            var indexed = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM indexed_packages;");
            var failed = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM indexed_packages WHERE status = 2;");
            var edges = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM hard_refs;");

            return new ReferenceIndexStatus(
                _databasePath,
                true,
                fingerprintMatches,
                complete,
                indexed,
                failed,
                edges,
                storedFingerprint);
        }
        catch
        {
            return new ReferenceIndexStatus(_databasePath, true, false, false, 0, 0, 0, null);
        }
    }

    public void Clear()
    {
        DeleteRequired(_databasePath);
        DeleteRequired(_databasePath + "-wal");
        DeleteRequired(_databasePath + "-shm");
    }

    public IReadOnlyList<string> QueryBackReferences(string targetCanonicalPackage, string? sourcePathFilter)
    {
        using var connection = OpenConnection(createIfMissing: false);
        EnsureSchema(connection);

        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(sourcePathFilter))
        {
            command.CommandText = "SELECT source FROM hard_refs WHERE target = $target ORDER BY source COLLATE NOCASE;";
        }
        else
        {
            command.CommandText = "SELECT source FROM hard_refs WHERE target = $target AND instr(lower(source), lower($filter)) > 0 ORDER BY source COLLATE NOCASE;";
            command.Parameters.AddWithValue("$filter", sourcePathFilter.Trim().Replace('\\', '/'));
        }
        command.Parameters.AddWithValue("$target", targetCanonicalPackage);

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public async Task<ReferenceIndexBuildResult> BuildAsync(
        DefaultFileProvider provider,
        IReadOnlyList<string> packagePaths,
        string currentFingerprint,
        int jobs,
        bool rebuild,
        bool retryFailed,
        Func<ResolvedObject, string?> canonicalizeResolvedObject)
    {
        var initialStatus = GetStatus(currentFingerprint);
        if (rebuild || (initialStatus.Exists && !initialStatus.FingerprintMatches))
        {
            if (initialStatus.Exists && !initialStatus.FingerprintMatches)
                Console.WriteLine("检测到鸣潮资源容器或索引结构发生变化，将重建反向引用索引。");
            Clear();
            initialStatus = GetStatus(currentFingerprint);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using (var setup = OpenConnection(createIfMissing: true))
        {
            EnsureSchema(setup);
            WriteMetadata(setup, "schema_version", SchemaVersion.ToString());
            WriteMetadata(setup, "container_fingerprint", currentFingerprint);
            WriteMetadata(setup, "complete", "0");

            if (retryFailed)
            {
                using var retry = setup.CreateCommand();
                retry.CommandText = "DELETE FROM indexed_packages WHERE status = 2;";
                retry.ExecuteNonQuery();
            }
        }

        var alreadyIndexed = LoadIndexedPackages();
        var pending = packagePaths
            .Where(path => !alreadyIndexed.Contains(path))
            .ToArray();

        Console.WriteLine($"反向引用索引：总包 {packagePaths.Count:N0}，已完成 {alreadyIndexed.Count:N0}，本次扫描 {pending.Length:N0}");
        Console.WriteLine($"索引文件：{_databasePath}");

        if (pending.Length == 0)
        {
            using var completeConnection = OpenConnection(createIfMissing: true);
            EnsureSchema(completeConnection);
            WriteMetadata(completeConnection, "complete", "1");
            var status = GetStatus(currentFingerprint);
            return new ReferenceIndexBuildResult(packagePaths.Count, alreadyIndexed.Count, 0, status.FailedPackages, status.EdgeCount, TimeSpan.Zero, _databasePath);
        }

        var channel = Channel.CreateBounded<PackageReferenceBatch>(new BoundedChannelOptions(Math.Max(32, jobs * 8))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var sw = Stopwatch.StartNew();
        var processed = 0;
        long discoveredEdges = 0;
        long lastProgress = 0;

        var writerTask = WriteBatchesAsync(channel.Reader);

        Exception? scanFailure = null;
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = jobs }, async (path, cancellationToken) =>
            {
                PackageReferenceBatch batch;
                try
                {
                    var package = await provider.LoadPackageAsync(path).ConfigureAwait(false);
                    var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var importCount = package.ImportMapLength;

                    for (var i = 0; i < importCount; i++)
                    {
                        ResolvedObject? resolved = null;
                        try
                        {
                            resolved = package.ResolvePackageIndex(new FPackageIndex(package, -(i + 1)));
                        }
                        catch
                        {
                            // A single broken import should not discard the package's other valid imports.
                        }

                        if (resolved is null) continue;
                        string? canonical = null;
                        try { canonical = canonicalizeResolvedObject(resolved); } catch { }
                        if (!string.IsNullOrWhiteSpace(canonical)) targets.Add(canonical);
                    }

                    Interlocked.Add(ref discoveredEdges, targets.Count);
                    batch = new PackageReferenceBatch(path, 1, importCount, targets.ToArray(), null);
                }
                catch (Exception ex)
                {
                    batch = new PackageReferenceBatch(path, 2, 0, Array.Empty<string>(), TrimError(ex.Message));
                }

                await channel.Writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                var done = Interlocked.Increment(ref processed);
                ReportProgress(done, pending.Length, Interlocked.Read(ref discoveredEdges), sw, ref lastProgress);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scanFailure = ex;
        }
        finally
        {
            channel.Writer.TryComplete(scanFailure);
        }

        await writerTask.ConfigureAwait(false);
        if (scanFailure is not null) throw scanFailure;

        sw.Stop();
        Console.WriteLine();

        using (var finish = OpenConnection(createIfMissing: true))
        {
            EnsureSchema(finish);
            WriteMetadata(finish, "complete", "1");
            WriteMetadata(finish, "completed_utc", DateTimeOffset.UtcNow.ToString("O"));
        }

        var finalStatus = GetStatus(currentFingerprint);
        return new ReferenceIndexBuildResult(
            packagePaths.Count,
            alreadyIndexed.Count,
            pending.Length,
            finalStatus.FailedPackages,
            finalStatus.EdgeCount,
            sw.Elapsed,
            _databasePath);
    }

    private HashSet<string> LoadIndexedPackages()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_databasePath)) return set;

        using var connection = OpenConnection(createIfMissing: false);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source FROM indexed_packages;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    private async Task WriteBatchesAsync(ChannelReader<PackageReferenceBatch> reader)
    {
        using var connection = OpenConnection(createIfMissing: true);
        EnsureSchema(connection);

        var transaction = connection.BeginTransaction();
        var sinceCommit = 0;

        using var deleteRefs = connection.CreateCommand();
        deleteRefs.CommandText = "DELETE FROM hard_refs WHERE source = $source;";
        var deleteSource = deleteRefs.Parameters.Add("$source", SqliteType.Text);

        using var insertRef = connection.CreateCommand();
        insertRef.CommandText = "INSERT OR IGNORE INTO hard_refs(target, source) VALUES($target, $source);";
        var insertTarget = insertRef.Parameters.Add("$target", SqliteType.Text);
        var insertSource = insertRef.Parameters.Add("$source", SqliteType.Text);

        using var upsertPackage = connection.CreateCommand();
        upsertPackage.CommandText = """
            INSERT INTO indexed_packages(source, status, import_count, edge_count, error)
            VALUES($source, $status, $imports, $edges, $error)
            ON CONFLICT(source) DO UPDATE SET
                status = excluded.status,
                import_count = excluded.import_count,
                edge_count = excluded.edge_count,
                error = excluded.error;
            """;
        var packageSource = upsertPackage.Parameters.Add("$source", SqliteType.Text);
        var packageStatus = upsertPackage.Parameters.Add("$status", SqliteType.Integer);
        var packageImports = upsertPackage.Parameters.Add("$imports", SqliteType.Integer);
        var packageEdges = upsertPackage.Parameters.Add("$edges", SqliteType.Integer);
        var packageError = upsertPackage.Parameters.Add("$error", SqliteType.Text);

        void BindTransaction()
        {
            deleteRefs.Transaction = transaction;
            insertRef.Transaction = transaction;
            upsertPackage.Transaction = transaction;
        }

        BindTransaction();

        await foreach (var batch in reader.ReadAllAsync().ConfigureAwait(false))
        {
            deleteSource.Value = batch.Source;
            deleteRefs.ExecuteNonQuery();

            if (batch.Status == 1)
            {
                insertSource.Value = batch.Source;
                foreach (var target in batch.Targets)
                {
                    insertTarget.Value = target;
                    insertRef.ExecuteNonQuery();
                }
            }

            packageSource.Value = batch.Source;
            packageStatus.Value = batch.Status;
            packageImports.Value = batch.ImportCount;
            packageEdges.Value = batch.Targets.Length;
            packageError.Value = (object?)batch.Error ?? DBNull.Value;
            upsertPackage.ExecuteNonQuery();

            sinceCommit++;
            if (sinceCommit >= 250)
            {
                transaction.Commit();
                transaction.Dispose();
                transaction = connection.BeginTransaction();
                BindTransaction();
                sinceCommit = 0;
            }
        }

        transaction.Commit();
        transaction.Dispose();
    }

    private SqliteConnection OpenConnection(bool createIfMissing)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA busy_timeout=60000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS indexed_packages(
                source TEXT COLLATE NOCASE PRIMARY KEY,
                status INTEGER NOT NULL,
                import_count INTEGER NOT NULL,
                edge_count INTEGER NOT NULL,
                error TEXT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS hard_refs(
                target TEXT COLLATE NOCASE NOT NULL,
                source TEXT COLLATE NOCASE NOT NULL,
                PRIMARY KEY(target, source)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_hard_refs_source ON hard_refs(source COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql)
        => checked((int)ExecuteScalarLong(connection, sql));

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static void ReportProgress(int done, int total, long edges, Stopwatch sw, ref long lastProgress)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastProgress);
        if ((done == total || now - previous >= 1000) && Interlocked.CompareExchange(ref lastProgress, now, previous) == previous)
        {
            var rate = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var remaining = total - done;
            var eta = rate > 0 ? TimeSpan.FromSeconds(remaining / rate) : TimeSpan.Zero;
            Console.Write($"\r{done,8:N0}/{total:N0}  引用边 {edges,10:N0}  {rate,7:N0}/s  ETA {eta:hh\\:mm\\:ss}   ");
        }
    }

    private static string TrimError(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private static void DeleteRequired(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        if (File.Exists(path)) throw new IOException($"无法删除旧索引文件：{path}");
    }

    private sealed record PackageReferenceBatch(string Source, int Status, int ImportCount, string[] Targets, string? Error);
}

internal static class AssetPathNormalizer
{
    public static string CanonicalFromPackageFile(string path)
    {
        var p = path.Trim().Trim('"').Replace('\\', '/');
        if (p.StartsWith("Client/Content/", StringComparison.OrdinalIgnoreCase))
            p = "/Game/" + p["Client/Content/".Length..];
        else if (p.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
            p = "/Engine/" + p["Engine/Content/".Length..];

        var extension = Path.GetExtension(p);
        if (!string.IsNullOrEmpty(extension)) p = p[..^extension.Length];
        return p.TrimEnd('/');
    }

    public static string? CanonicalFromResolvedObjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Replace('\\', '/');

        var firstQuote = p.IndexOf('\'');
        var lastQuote = p.LastIndexOf('\'');
        if (firstQuote >= 0 && lastQuote > firstQuote)
            p = p[(firstQuote + 1)..lastQuote];
        else
            p = p.Trim('\'');

        if (p.StartsWith("Client/Content/", StringComparison.OrdinalIgnoreCase))
            p = "/Game/" + p["Client/Content/".Length..];
        else if (p.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
            p = "/Engine/" + p["Engine/Content/".Length..];

        if (!p.StartsWith("/", StringComparison.Ordinal)) return null;
        if (p.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/Memory/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/Temp/", StringComparison.OrdinalIgnoreCase))
            return null;

        var slash = p.LastIndexOf('/');
        var dot = p.IndexOf('.', Math.Max(0, slash + 1));
        var colon = p.IndexOf(':', Math.Max(0, slash + 1));
        var cut = -1;
        if (dot >= 0 && colon >= 0) cut = Math.Min(dot, colon);
        else if (dot >= 0) cut = dot;
        else if (colon >= 0) cut = colon;
        if (cut >= 0) p = p[..cut];

        var extension = Path.GetExtension(p);
        if (extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || extension.Equals(".umap", StringComparison.OrdinalIgnoreCase))
            p = p[..^extension.Length];

        return p.TrimEnd('/');
    }
}