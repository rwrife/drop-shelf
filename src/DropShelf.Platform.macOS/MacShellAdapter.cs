namespace DropShelf.Platform.macOS;

public enum MacShellError { None, ShortcutConflict, PermissionDenied, Unavailable, InvalidTarget, TargetMissing, Failed }
public readonly record struct MacShellOperationResult(MacShellError Error)
{
    public bool Succeeded => Error == MacShellError.None;
    public static MacShellOperationResult Success => new(MacShellError.None);
}
public enum MacNativeResult { Success, Conflict, PermissionDenied, Unavailable, InvalidTarget, TargetMissing, Failed }

public interface IMacShellApi
{
    MacNativeResult RegisterShortcut(string shortcut);
    void UnregisterShortcut(string shortcut);
    MacNativeResult Open(Uri target) => MacNativeResult.Unavailable;
    MacNativeResult Reveal(string path) => MacNativeResult.Unavailable;
    MacNativeResult SetLaunchAtLogin(bool enabled) => MacNativeResult.Unavailable;

}

public sealed class MacShellAdapter(IMacShellApi api) : IDisposable
{
    private readonly IMacShellApi api = api ?? throw new ArgumentNullException(nameof(api));
    public string? RegisteredShortcut { get; private set; }

    public MacShellOperationResult ConfigureShortcut(string shortcut)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcut);
        MacNativeResult result = api.RegisterShortcut(shortcut);
        if (result != MacNativeResult.Success)
        {
            return new(Map(result));
        }
        string? previous = RegisteredShortcut;
        RegisteredShortcut = shortcut;
        if (previous is not null && !StringComparer.Ordinal.Equals(previous, shortcut))
        {
            api.UnregisterShortcut(previous);
        }
        return MacShellOperationResult.Success;
    }

    public MacShellOperationResult Open(string target)
        => !TryValidatedTarget(target, out Uri? uri)
            ? new(MacShellError.InvalidTarget)
            : uri!.IsFile && !File.Exists(uri.LocalPath) && !Directory.Exists(uri.LocalPath)
            ? new(MacShellError.TargetMissing)
            : new(Map(api.Open(uri)));

    public MacShellOperationResult Reveal(string path)
        => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            ? new(MacShellError.InvalidTarget)
            : !File.Exists(path) && !Directory.Exists(path)
            ? new(MacShellError.TargetMissing)
            : new(Map(api.Reveal(path)));

    public MacShellOperationResult SetLaunchAtLogin(bool enabled) => new(Map(api.SetLaunchAtLogin(enabled)));

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

    private static MacShellError Map(MacNativeResult result) => result switch
    {
        MacNativeResult.Success => MacShellError.None,
        MacNativeResult.Conflict => MacShellError.ShortcutConflict,
        MacNativeResult.PermissionDenied => MacShellError.PermissionDenied,
        MacNativeResult.Unavailable => MacShellError.Unavailable,
        MacNativeResult.InvalidTarget => MacShellError.InvalidTarget,
        MacNativeResult.TargetMissing => MacShellError.TargetMissing,
        MacNativeResult.Failed => MacShellError.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}
