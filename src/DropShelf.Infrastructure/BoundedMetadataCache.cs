namespace DropShelf.Infrastructure;

public sealed record CacheCleanupResult(int RemovedEntries, int RetainedEntries, long RetainedBytes);

public sealed class BoundedMetadataCache(string directory, long maximumBytes = 32 * 1024 * 1024, int maximumEntries = 256)
{
    private readonly string directory = ValidateDirectory(directory);
    private readonly long maximumBytes = maximumBytes > 0
        ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private readonly int maximumEntries = maximumEntries > 0
        ? maximumEntries
        : throw new ArgumentOutOfRangeException(nameof(maximumEntries));

    public CacheCleanupResult Cleanup()
    {
        _ = Directory.CreateDirectory(directory);
        FileInfo[] files =
        [
            .. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal),
        ];
        int retainedEntries = 0;
        int removedEntries = 0;
        long retainedBytes = 0;
        foreach (FileInfo file in files)
        {
            bool retain = retainedEntries < maximumEntries && file.Length <= maximumBytes - retainedBytes;
            if (retain)
            {
                retainedEntries++;
                retainedBytes += file.Length;
            }
            else
            {
                file.Delete();
                removedEntries++;
            }
        }
        return new(removedEntries, retainedEntries, retainedBytes);
    }

    public void Clear()
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }

    private static string ValidateDirectory(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A cache directory is required.", nameof(value))
        : Path.GetFullPath(value);
}
