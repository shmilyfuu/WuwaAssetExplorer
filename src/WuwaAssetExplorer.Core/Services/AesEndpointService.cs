using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WuwaAssetExplorer.Core.Models;

namespace WuwaAssetExplorer.Core.Services;

public sealed class AesEndpointService
{
    public const string DefaultEndpoint = "https://yarik0chka.github.io/wuwa-keys/keys.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public AesEndpointService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WuwaAssetExplorer/0.1");
    }

    public async Task<AesFetchResult> GetKeysAsync(
        string endpoint,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        PortablePaths.EnsureDataDirectory();

        if (!forceRefresh && File.Exists(PortablePaths.AesCacheFile))
        {
            var cached = await ReadCacheAsync(cancellationToken);
            if (cached is not null && cached.Endpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - cached.RetrievedAt < TimeSpan.FromHours(6))
            {
                return new AesFetchResult(cached.Keys, true, cached.RetrievedAt);
            }
        }

        try
        {
            var payload = await _httpClient.GetFromJsonAsync<EndpointPayload>(endpoint, JsonOptions, cancellationToken)
                          ?? throw new InvalidDataException("AES Endpoint 返回了空内容。");
            var keys = Validate(payload);
            var cache = new AesCache(endpoint, DateTimeOffset.UtcNow, keys);
            await WriteCacheAsync(cache, cancellationToken);
            return new AesFetchResult(keys, false, cache.RetrievedAt);
        }
        catch when (File.Exists(PortablePaths.AesCacheFile))
        {
            var cached = await ReadCacheAsync(cancellationToken);
            if (cached is not null)
            {
                return new AesFetchResult(cached.Keys, true, cached.RetrievedAt);
            }
            throw;
        }
    }

    private static AesKeySet Validate(EndpointPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.MainKey) || payload.MainKey.Length != 66 || !payload.MainKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AES Endpoint 的 mainKey 格式无效。");

        var dynamicKeys = (payload.DynamicKeys ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Guid) && !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => new DynamicAesKey(NormalizeGuid(x.Guid!), x.Key!))
            .ToArray();

        return new AesKeySet(payload.MainKey, dynamicKeys);
    }

    private static string NormalizeGuid(string guid)
    {
        var normalized = new string(guid.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 32) throw new InvalidDataException($"动态 AES GUID 格式无效：{guid}");
        return normalized;
    }

    private static async Task<AesCache?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(PortablePaths.AesCacheFile);
        return await JsonSerializer.DeserializeAsync<AesCache>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteCacheAsync(AesCache cache, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(PortablePaths.AesCacheFile);
        await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken);
    }

    private sealed record AesCache(string Endpoint, DateTimeOffset RetrievedAt, AesKeySet Keys);

    private sealed class EndpointPayload
    {
        [JsonPropertyName("mainKey")]
        public string? MainKey { get; init; }

        [JsonPropertyName("dynamicKeys")]
        public DynamicKeyPayload[]? DynamicKeys { get; init; }
    }

    private sealed class DynamicKeyPayload
    {
        [JsonPropertyName("guid")]
        public string? Guid { get; init; }

        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
