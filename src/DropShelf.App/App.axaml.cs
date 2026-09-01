using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DropShelf.Core;
using DropShelf.Infrastructure;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics.CodeAnalysis;
using Win = DropShelf.Platform.Windows;
using Mac = DropShelf.Platform.macOS;

namespace DropShelf.App;

public enum HostOperatingSystem { Windows, MacOS, Unsupported }

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Path.Combine(localData, "DropShelf");
            string databasePath = Path.Combine(appData, "shelf.db");
            SqliteShelfStore store = new(databasePath);
            ShelfDataService dataService = new(store);
            BoundedMetadataCache metadataCache = new(Path.Combine(appData, "cache"));
            try { _ = metadataCache.Cleanup(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            MainWindow? window = null;
            INativeShell nativeShell = CreateNativeShellForHost(() => Dispatcher.UIThread.Post(() => window?.ToggleVisibilityForHost()));
            window = new MainWindow(AppSettings.Default, new NativeShelfItemActions(nativeShell),
                retryShelfLoad: () => LoadAndApplyStartupDataForHostAsync(window!, dataService, store, nativeShell));
            window.ConfigureLocalDataForHost(dataService, AppSettings.Default);
            window.ConfigureRecoveryForHost(store, metadataCache);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                try { _ = window.PrepareForExitForHostAsync().GetAwaiter().GetResult(); } catch { }
                nativeShell.Dispose();
                store.Dispose();
            };
            ConfigureTrayForHost(desktop, window);
            _ = LoadAndApplyStartupDataForHostAsync(window, dataService, store, nativeShell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    [UnconditionalSuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "The factory is selected only after RuntimeInformation OS detection.")]
    public static INativeShell CreateNativeShellForHost(Action? shortcutActivated = null)
    {
        Action activated = shortcutActivated ?? (() => { });
        HostOperatingSystem operatingSystem = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? HostOperatingSystem.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? HostOperatingSystem.MacOS : HostOperatingSystem.Unsupported;
        return CreateNativeShellForHost(operatingSystem,
            () => CreateWindowsNativeShell(activated),
            () => new MacNativeShell(new Mac.MacShellAdapter(new Mac.SystemMacShellApi(activated))));
    }

    [SupportedOSPlatform("windows")]
    private static WindowsNativeShell CreateWindowsNativeShell(Action activated) =>
        new(new Win.WindowsShellAdapter(new Win.SystemWindowsShellApi(activated)));

    public static INativeShell CreateNativeShellForHost(HostOperatingSystem operatingSystem,
        Func<INativeShell> windowsFactory, Func<INativeShell> macFactory)
    {
        ArgumentNullException.ThrowIfNull(windowsFactory);
        ArgumentNullException.ThrowIfNull(macFactory);
        try
        {
            return operatingSystem switch
            {
                HostOperatingSystem.Windows => windowsFactory(),
                HostOperatingSystem.MacOS => macFactory(),
                HostOperatingSystem.Unsupported => new UnavailableNativeShell(),
                _ => throw new ArgumentOutOfRangeException(nameof(operatingSystem)),
            };
        }
        catch
        {
            return new UnavailableNativeShell();
        }
    }

    public static NativeShellStatus ConfigureShortcutForHost(MainWindow window, INativeShell nativeShell, string shortcut)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(nativeShell);
        NativeShellStatus status = nativeShell.ConfigureShortcut(shortcut);
        window.ShowNativeShellStatus(status);
        return status;
    }

    private static void ConfigureTrayForHost(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        NativeMenuItem show = new("Show or hide shelf");
        show.Click += (_, _) =>
        {
            if (window.IsVisible)
            {
                window.Hide();
            }
            else
            {
                window.Show();
                window.Activate();
            }
        };
        NativeMenuItem quit = new("Quit Drop Shelf");
        quit.Click += (_, _) => desktop.Shutdown();
        WindowIcon icon = CreateShellIcon();
        window.Icon = icon;
        TrayIcon trayIcon = new()
        {
            Icon = icon,
            ToolTipText = "Drop Shelf",
            Menu = new NativeMenu { Items = { show, quit } },
            IsVisible = true,
        };
        desktop.Exit += (_, _) => trayIcon.Dispose();
    }

    private static WindowIcon CreateShellIcon()
    {
        const int side = 16;
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0x4D42);
            writer.Write(54 + (side * side * 4));
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(side);
            writer.Write(side);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(side * side * 4);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    bool shelf = y is >= 4 and <= 6 || y is >= 10 and <= 12 || x is 3 or 12;
                    writer.Write(shelf ? (byte)255 : (byte)103);
                    writer.Write(shelf ? (byte)255 : (byte)80);
                    writer.Write(shelf ? (byte)255 : (byte)45);
                    writer.Write((byte)255);
                }
            }
        }
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public static Task<AppSettings> LoadStartupSettingsForHostAsync(
        ISettingsStore settingsStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        return settingsStore.LoadSettingsAsync(cancellationToken);
    }

    public static Func<Task> CreateRetryShelfLoadForHost(
        MainWindow window, ISettingsStore settingsStore, INativeShell nativeShell) =>
        () => LoadAndApplyStartupSettingsForHostAsync(window, settingsStore, nativeShell);

    public static async Task LoadAndApplyStartupDataForHostAsync(
        MainWindow window, ShelfDataService dataService, ISettingsStore? settingsStore = null,
        INativeShell? nativeShell = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(dataService);
        window.SetLocalDataLoadingForHost(true);
        window.ShowShelfState(ShelfUiState.Loading);
        try
        {
            ShelfLoadResult loaded = await dataService.LoadAsync(cancellationToken);
            window.ConfigureLocalDataForHost(dataService, loaded.Snapshot.Settings);
            window.ApplySnapshotForHost(loaded.Snapshot);
            NativeShellStatus? shortcutStatus = null;
            if (nativeShell is not null && settingsStore is not null)
            {
                window.ConfigureNativeSettingsForHost(settingsStore, nativeShell, loaded.Snapshot.Settings);
                shortcutStatus = ConfigureShortcutForHost(window, nativeShell, loaded.Snapshot.Settings.GlobalShortcut);
            }
            window.SetLocalDataLoadingForHost(false);
            window.ShowShelfState(loaded.ExpiredItems > 0 ? ShelfUiState.Expired : ShelfUiState.Ready);
            if (shortcutStatus is not null and not NativeShellStatus.Success)
            {
                window.ShowNativeShellStatus(shortcutStatus.Value);
            }
        }
        catch
        {
            window.ShowShelfState(ShelfUiState.RecoverableError);
        }
    }

    public static async Task LoadAndApplyStartupSettingsForHostAsync(
        MainWindow window, ISettingsStore settingsStore, INativeShell? nativeShell = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowShelfState(ShelfUiState.Loading);
        try
        {
            AppSettings settings = await LoadStartupSettingsForHostAsync(settingsStore, cancellationToken);
            window.ApplySettings(settings);
            NativeShellStatus? shortcutStatus = null;
            if (nativeShell is not null)
            {
                window.ConfigureNativeSettingsForHost(settingsStore, nativeShell, settings);
                shortcutStatus = ConfigureShortcutForHost(window, nativeShell, settings.GlobalShortcut);
            }
            window.ShowShelfState(ShelfUiState.Ready);
            if (shortcutStatus is not null and not NativeShellStatus.Success)
            {
                window.ShowNativeShellStatus(shortcutStatus.Value);
            }
        }
        catch
        {
            window.ShowShelfState(ShelfUiState.RecoverableError);
        }
    }
}
