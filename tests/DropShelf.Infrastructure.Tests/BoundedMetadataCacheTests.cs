using Xunit;

namespace DropShelf.Infrastructure.Tests;

public sealed class BoundedMetadataCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DropShelfCacheTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupDeterministicallyKeepsNewestBoundedAppOwnedEntriesOnly()
    {
        string cache = Path.Combine(root, "cache");
        string source = Path.Combine(root, "source.txt");
        _ = Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(source, "source remains");
        string oldest = await WriteCacheFileAsync(cache, "oldest.cache", "1111", 1);
        string middle = await WriteCacheFileAsync(cache, "middle.cache", "2222", 2);
        string newest = await WriteCacheFileAsync(cache, "newest.cache", "3333", 3);
        BoundedMetadataCache bounded = new(cache, maximumBytes: 8, maximumEntries: 2);

        CacheCleanupResult result = bounded.Cleanup();

        Assert.Equal(1, result.RemovedEntries);
        Assert.Equal(8, result.RetainedBytes);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.Equal("source remains", await File.ReadAllTextAsync(source));
    }

    private static async Task<string> WriteCacheFileAsync(string cache, string name, string content, int minute)
    {
        string path = Path.Combine(cache, name);
        await File.WriteAllTextAsync(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
