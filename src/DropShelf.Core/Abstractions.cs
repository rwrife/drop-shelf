namespace DropShelf.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
}
public interface IShelfStore
{
    Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StoreSnapshot snapshot, CancellationToken cancellationToken = default);
}
public interface ISettingsStore
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
public sealed record StoreSnapshot(IReadOnlyList<ShelfItem> Items, AppSettings Settings);
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
}
