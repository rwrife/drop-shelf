namespace DropShelf.Core;

public enum DockEdge { Left, Right, Top, Bottom }

public sealed record AppSettings
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    public const string DefaultGlobalShortcut = "Ctrl+Alt+Space";
    public static IReadOnlyList<string> SupportedGlobalShortcuts { get; } =
        [DefaultGlobalShortcut, "Ctrl+Shift+Space", "Ctrl+Alt+D", "Ctrl+Shift+D"];
    public static AppSettings Default { get; } = Create();
    private AppSettings(DockEdge dockEdge, TimeSpan retention, bool startAtLogin, bool reduceMotion, bool highContrast, string globalShortcut) =>
        (DockEdge, Retention, StartAtLogin, ReduceMotion, HighContrast, GlobalShortcut) = (dockEdge, retention, startAtLogin, reduceMotion, highContrast, globalShortcut);
    public DockEdge DockEdge { get; }
    public TimeSpan Retention { get; }
    public bool StartAtLogin { get; }
    public bool ReduceMotion { get; }
    public bool HighContrast { get; }
    public string GlobalShortcut { get; }

    public static AppSettings Create(DockEdge dockEdge = DockEdge.Right, TimeSpan? retention = null,
        bool startAtLogin = false, bool reduceMotion = false, bool highContrast = false,
        string globalShortcut = DefaultGlobalShortcut)
    {
        if (!Enum.IsDefined(dockEdge))
        {
            throw Input.Error(ValidationErrorCode.InvalidSettings, nameof(DockEdge), "Dock edge is invalid.");
        }

        if (!SupportedGlobalShortcuts.Contains(globalShortcut, StringComparer.Ordinal))
        {
            throw Input.Error(ValidationErrorCode.InvalidSettings, nameof(GlobalShortcut), "Global shortcut is not supported.");
        }

        TimeSpan effective = retention ?? DefaultRetention;
        return effective < TimeSpan.FromMinutes(1) || effective > TimeSpan.FromDays(30)
            ? throw Input.Error(ValidationErrorCode.InvalidSettings, nameof(Retention), "Retention must be between one minute and 30 days.")
            : new(dockEdge, effective, startAtLogin, reduceMotion, highContrast, globalShortcut);
    }
}
