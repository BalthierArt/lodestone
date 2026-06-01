using System.Collections.Concurrent;
using System.Net;
using Dalamud.Interface.Textures.TextureWraps;

namespace Lodestone.Services;

public sealed class ImageCache : IDisposable
{
    public const string AssetScheme = "asset://";
    private const int MaxFailures = 3;

    private readonly HttpClient httpClient = new();
    private readonly ConcurrentDictionary<string, ImageEntry> cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> downloads = new();
    private readonly ConcurrentDictionary<string, int> failures = new();
    private readonly SemaphoreSlim downloadLimiter = new(4);

    public ImageCache()
    {
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LodestoneDalamudPlugin/0.1");
    }

    public int CachedTextureCount => cache.Count;
    public int ActiveDownloadCount => downloads.Count;
    public int FailedImageCount => failures.Count;

    public IDalamudTextureWrap? GetTexture(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (cache.TryGetValue(url, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt && entry.Texture != null)
                return entry.Texture;

            entry.Texture?.Dispose();
            cache.TryRemove(url, out _);
        }

        if (IsKnownBlockedImageUrl(url))
        {
            failures.TryAdd(url, MaxFailures);
            return null;
        }

        if (failures.TryGetValue(url, out var failCount) && failCount >= MaxFailures)
            return null;

        _ = downloads.GetOrAdd(url, key => new Lazy<Task>(() => StartDownloadAsync(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return null;
    }

    public void Clear()
    {
        foreach (var entry in cache.Values)
            entry.Texture?.Dispose();

        cache.Clear();
        failures.Clear();
    }

    private Task StartDownloadAsync(string url)
    {
        return Task.Run(async () =>
        {
            await downloadLimiter.WaitAsync();
            try
            {
                byte[] bytes;
                if (url.StartsWith(AssetScheme, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = url[AssetScheme.Length..];
                    var path = AssetDirectories()
                        .Select(directory => Path.Combine(directory, "Assets", fileName))
                        .FirstOrDefault(File.Exists);
                    if (path == null)
                        throw new FileNotFoundException($"Unable to find asset {fileName}.");

                    bytes = await File.ReadAllBytesAsync(path);
                }
                else
                {
                    var response = await httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Image request failed with {(int)response.StatusCode} {response.StatusCode}.", null, response.StatusCode);

                    bytes = await response.Content.ReadAsByteArrayAsync();
                }

                if (bytes.Length == 0)
                    throw new InvalidDataException("Image response was empty.");

                var texture = await Plugin.TextureProvider.CreateFromImageAsync(bytes);
                cache[url] = new ImageEntry(texture, DateTime.UtcNow.AddHours(6));
                failures.TryRemove(url, out _);
            }
            catch (Exception ex)
            {
                var failureCount = IsPermanentFailure(ex)
                    ? MaxFailures
                    : failures.AddOrUpdate(url, 1, (_, count) => Math.Min(MaxFailures, count + 1));
                failures[url] = failureCount;
                if (failureCount == 1 || IsPermanentFailure(ex))
                    Plugin.Log.Warning(ex, "Failed to download Lodestone image {Url}", url);
            }
            finally
            {
                downloadLimiter.Release();
                downloads.TryRemove(url, out _);
            }
        });
    }

    public static bool IsKnownBlockedImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("ffxiv.gamerescape.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/w/images", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermanentFailure(Exception ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized };

    private static IEnumerable<string> AssetDirectories()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            yield return assemblyDirectory;

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            yield return AppContext.BaseDirectory;

        var assemblyLocation = Plugin.PluginInterface.GetType().GetProperty("AssemblyLocation")?.GetValue(Plugin.PluginInterface);
        if (assemblyLocation is FileInfo fileInfo && fileInfo.DirectoryName is { Length: > 0 } fileDirectory)
            yield return fileDirectory;
        else if (assemblyLocation is string locationString && Path.GetDirectoryName(locationString) is { Length: > 0 } stringDirectory)
            yield return stringDirectory;
    }

    public void Dispose()
    {
        foreach (var entry in cache.Values)
            entry.Texture?.Dispose();

        cache.Clear();
        httpClient.Dispose();
        downloadLimiter.Dispose();
    }

    private sealed record ImageEntry(IDalamudTextureWrap Texture, DateTime ExpiresAt);
}
