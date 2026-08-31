using DropShelf.Core;

namespace DropShelf.App;

public enum NativeShellStatus { Success, Conflict, PermissionDenied, Unavailable, InvalidTarget, TargetMissing, Failed }

public interface INativeShell : IDisposable
{
    NativeShellStatus ConfigureShortcut(string shortcut);
    NativeShellStatus Open(string target);
    NativeShellStatus Reveal(string path);
    NativeShellStatus SetLaunchAtLogin(bool enabled);
}

public sealed class NativeShelfItemActions(INativeShell shell, IFileSystem? fileSystem = null) : IShelfItemActions
{
    private readonly INativeShell shell = shell ?? throw new ArgumentNullException(nameof(shell));
    private readonly IFileSystem fileSystem = fileSystem ?? new SystemFileSystem();

    public Task CopyAsync(IReadOnlyList<ShelfItem> items) => throw new InvalidOperationException("Native copy is unavailable.");

    public Task OpenAsync(IReadOnlyList<ShelfItem> items) => RunAsync(items, OpenTarget, shell.Open);

    public Task RevealAsync(IReadOnlyList<ShelfItem> items) => RunAsync(items, RevealTarget, shell.Reveal);

    private static Task RunAsync(IReadOnlyList<ShelfItem> items, Func<ShelfItem, string?> targetFor, Func<string, NativeShellStatus> action)
    {
        ArgumentNullException.ThrowIfNull(items);
        string?[] targets = items.Select(targetFor).ToArray();
        if (targets.Any(target => target is null))
        {
            return Task.FromException(new InvalidOperationException("The native action could not be completed."));
        }

        bool succeeded = true;
        foreach (string target in targets.Cast<string>())
        {
            if (action(target) != NativeShellStatus.Success)
            {
                succeeded = false;
            }
        }
        return succeeded ? Task.CompletedTask : Task.FromException(new InvalidOperationException("The native action could not be completed."));
    }

    private string? OpenTarget(ShelfItem item) => item.Payload switch
    {
        FileReferencePayload file when Exists(file) => file.Path,
        UrlPayload url when url.Url.Scheme is "http" or "https" or "file" &&
            string.IsNullOrEmpty(url.Url.UserInfo) &&
            (!url.Url.IsFile || fileSystem.FileExists(url.Url.LocalPath) || fileSystem.DirectoryExists(url.Url.LocalPath)) =>
            url.Url.AbsoluteUri,
        _ => null,
    };

    private string? RevealTarget(ShelfItem item) => item.Payload is FileReferencePayload file && Exists(file) ? file.Path : null;
    private bool Exists(FileReferencePayload file) => file.Availability != FileAvailability.Missing &&
        (fileSystem.FileExists(file.Path) || fileSystem.DirectoryExists(file.Path));
}


public sealed class WindowsNativeShell(Platform.Windows.WindowsShellAdapter adapter) : INativeShell
{
    private readonly Platform.Windows.WindowsShellAdapter adapter = adapter;
    public NativeShellStatus ConfigureShortcut(string shortcut) => Map(adapter.ConfigureShortcut(shortcut).Error);
    public NativeShellStatus Open(string target) => Map(adapter.Open(target).Error);
    public NativeShellStatus Reveal(string path) => Map(adapter.Reveal(path).Error);
    public NativeShellStatus SetLaunchAtLogin(bool enabled) => Map(adapter.SetLaunchAtLogin(enabled).Error);
    public void Dispose() => adapter.Dispose();
    private static NativeShellStatus Map(Platform.Windows.ShellError error) => (NativeShellStatus)(int)error;
}

public sealed class MacNativeShell(Platform.macOS.MacShellAdapter adapter) : INativeShell
{
    private readonly Platform.macOS.MacShellAdapter adapter = adapter;
    public NativeShellStatus ConfigureShortcut(string shortcut) => Map(adapter.ConfigureShortcut(shortcut).Error);
    public NativeShellStatus Open(string target) => Map(adapter.Open(target).Error);
    public NativeShellStatus Reveal(string path) => Map(adapter.Reveal(path).Error);
    public NativeShellStatus SetLaunchAtLogin(bool enabled) => Map(adapter.SetLaunchAtLogin(enabled).Error);
    public void Dispose() => adapter.Dispose();
    private static NativeShellStatus Map(Platform.macOS.MacShellError error) => (NativeShellStatus)(int)error;
}

public sealed class UnavailableNativeShell : INativeShell
{
    public NativeShellStatus ConfigureShortcut(string shortcut) => NativeShellStatus.Unavailable;
    public NativeShellStatus Open(string target) => NativeShellStatus.Unavailable;
    public NativeShellStatus Reveal(string path) => NativeShellStatus.Unavailable;
    public NativeShellStatus SetLaunchAtLogin(bool enabled) => NativeShellStatus.Unavailable;
    public void Dispose() { }
}
