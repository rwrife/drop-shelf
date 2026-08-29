namespace DropShelf.Platform.Windows;

public enum ShellError { None, ShortcutConflict, PermissionDenied, Unavailable, InvalidTarget, TargetMissing, Failed }

public readonly record struct ShellOperationResult(ShellError Error)
{
    public bool Succeeded => Error == ShellError.None;
    public static ShellOperationResult Success => new(ShellError.None);
}

public enum WindowsNativeResult { Success, Conflict, PermissionDenied, Unavailable, InvalidTarget, TargetMissing, Failed }

public interface IWindowsShellApi
{
    WindowsNativeResult RegisterShortcut(string shortcut);
    void UnregisterShortcut(string shortcut);
    WindowsNativeResult Open(Uri target) => WindowsNativeResult.Unavailable;
    WindowsNativeResult Reveal(string path) => WindowsNativeResult.Unavailable;
    WindowsNativeResult SetLaunchAtLogin(bool enabled) => WindowsNativeResult.Unavailable;
}

public sealed class WindowsShellAdapter(IWindowsShellApi api) : IDisposable
{
    private readonly IWindowsShellApi api = api ?? throw new ArgumentNullException(nameof(api));

    public string? RegisteredShortcut { get; private set; }

    public ShellOperationResult ConfigureShortcut(string shortcut)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcut);
        WindowsNativeResult nativeResult = api.RegisterShortcut(shortcut);
        if (nativeResult != WindowsNativeResult.Success)
        {
            return new(Map(nativeResult));
        }

        string? previous = RegisteredShortcut;
        RegisteredShortcut = shortcut;
        if (previous is not null && !StringComparer.Ordinal.Equals(previous, shortcut))
        {
            api.UnregisterShortcut(previous);
        }
        return ShellOperationResult.Success;
    }

    public ShellOperationResult Open(string target)
        => !TryValidatedTarget(target, out Uri? uri)
            ? new(ShellError.InvalidTarget)
            : uri!.IsFile && !File.Exists(uri.LocalPath) && !Directory.Exists(uri.LocalPath)
            ? new(ShellError.TargetMissing)
            : new(Map(api.Open(uri)));

    public ShellOperationResult Reveal(string path)
        => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            ? new(ShellError.InvalidTarget)
            : !File.Exists(path) && !Directory.Exists(path)
            ? new(ShellError.TargetMissing)
            : new(Map(api.Reveal(path)));

    public ShellOperationResult SetLaunchAtLogin(bool enabled) => new(Map(api.SetLaunchAtLogin(enabled)));

    public void Dispose()
    {
        if (api is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static bool TryValidatedTarget(string target, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        if (Path.IsPathFullyQualified(target))
        {
            uri = new Uri(target);
            return true;
        }
        return Uri.TryCreate(target, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    private static ShellError Map(WindowsNativeResult result) => result switch
    {
        WindowsNativeResult.Success => ShellError.None,
        WindowsNativeResult.Conflict => ShellError.ShortcutConflict,
        WindowsNativeResult.PermissionDenied => ShellError.PermissionDenied,
        WindowsNativeResult.Unavailable => ShellError.Unavailable,
        WindowsNativeResult.InvalidTarget => ShellError.InvalidTarget,
        WindowsNativeResult.TargetMissing => ShellError.TargetMissing,
        WindowsNativeResult.Failed => ShellError.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}
