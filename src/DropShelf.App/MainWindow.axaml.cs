using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Platform;
using DropShelf.Core;
using DropShelf.Infrastructure;
using DropShelf.Platform.macOS;
using DropShelf.Platform.Windows;
using System.Runtime.InteropServices;

namespace DropShelf.App;

public readonly record struct StorageDropReference(string Path, FileReferenceKind Kind);
public enum ShelfUiState { Ready, Loading, Expired, Unavailable, RecoverableError }

public sealed class OutboundDataTransfer : IDisposable
{
    private IReadOnlyList<IStorageItem>? resolvedItems;
    private int disposed;

    internal OutboundDataTransfer(IDataTransfer data, IReadOnlyList<IStorageItem>? resolvedItems = null)
    {
        Data = data;
        this.resolvedItems = resolvedItems;
    }

    public IDataTransfer Data { get; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        IReadOnlyList<IStorageItem>? items = Interlocked.Exchange(ref resolvedItems, null);
        if (items is null)
        {
            return;
        }

        DisposeStorageItems(items);
    }

    internal static void DisposeStorageItems(IEnumerable<IStorageItem> items)
    {
        foreach (IStorageItem item in items)
        {
            try
            {
                item.Dispose();
            }
            catch
            {
                // Storage handles are best-effort cleanup at an untrusted host boundary.
            }
        }
    }
}

public sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyList<RetentionChoice> RetentionChoices =
    [
        new("Keep for 1 hour", TimeSpan.FromHours(1), false),
        new("Keep for 24 hours", TimeSpan.FromHours(24), false),
        new("Keep for 7 days", TimeSpan.FromDays(7), false),
        new("Keep for 30 days", TimeSpan.FromDays(30), false),
        new("Clear unpinned on app exit", AppSettings.DefaultRetention, true),
    ];
    private readonly DropAdmissionService admission;
    private Point? dragStart;
    private bool dragInProgress;
    private DockEdge dockEdge;
    private Screens? activeScreens;
    private readonly Func<Task>? retryShelfLoad;
    private ShelfBounds? expandedBounds;
    private bool retryInProgress;
    private bool localDataLoading;
    private ISettingsStore? nativeSettingsStore;
    private INativeShell? nativeSettingsShell;
    private AppSettings nativeSettings = AppSettings.Default;
    private bool updatingNativeControls;
    private bool nativeSettingsUpdateInProgress;
    private bool recoveringDock;
    private ShelfDataService? dataService;
    private AppSettings currentSettings;
    private Task pendingPersistence = Task.CompletedTask;
    private IResettableShelfStore? resettableStore;
    private BoundedMetadataCache? metadataCache;

    public MainWindow()
        : this(AppSettings.Default, null, null)
    {
    }

    public MainWindow(IShelfItemActions? actions)
        : this(AppSettings.Default, actions, null)
    {
    }

    public MainWindow(IShelfItemActions? actions, Func<Task>? retryShelfLoad)
        : this(AppSettings.Default, actions, retryShelfLoad)
    {
    }

    public MainWindow(AppSettings settings, IShelfItemActions? actions = null, Func<Task>? retryShelfLoad = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AvaloniaXamlLoader.Load(this);
        dockEdge = settings.DockEdge;
        currentSettings = settings;
        this.retryShelfLoad = retryShelfLoad;
        Session = new ShelfSession();
        ViewModel = new ShelfViewModel(Session, DateTimeOffset.UtcNow, actions);
        admission = new(new CanonicalDropConverter(), Session);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDropHandler(this, OnDrop);
        AddHandler(KeyDownEvent, OnShelfKeyDown);
        this.FindControl<Button>("MoveUpButton")!.Click += (_, _) => RunMutatingAndRender(() => _ = ViewModel.MoveSelected(-1));
        this.FindControl<Button>("MoveDownButton")!.Click += (_, _) => RunMutatingAndRender(() => _ = ViewModel.MoveSelected(1));
        this.FindControl<Button>("CopyButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.CopySelectedAsync);
        this.FindControl<Button>("OpenButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.OpenSelectedAsync);
        this.FindControl<Button>("RevealButton")!.Click += async (_, _) => await RunViewModelActionAsync(ViewModel.RevealSelectedAsync);
        this.FindControl<Button>("PinButton")!.Click += (_, _) => RunMutatingAndRender(ViewModel.TogglePinned);
        this.FindControl<Button>("RemoveButton")!.Click += (_, _) => RunMutatingAndRender(() => _ = ViewModel.RemoveSelected());
        this.FindControl<Button>("ClearButton")!.Click += async (_, _) => await RunDataActionAsync(ClearAllForHostAsync);
        this.FindControl<Button>("ClearUnpinnedButton")!.Click += async (_, _) => await RunDataActionAsync(async () => _ = await ClearUnpinnedForHostAsync());
        this.FindControl<Button>("CollapseButton")!.Click += (_, _) => SetCollapsed(true);
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => SetCollapsed(false);
        this.FindControl<Button>("RetryButton")!.Click += OnRetryShelfLoad;
        this.FindControl<Button>("ResetLocalDataButton")!.Click += OnResetLocalData;
        this.FindControl<ComboBox>("ShortcutPicker")!.ItemsSource = AppSettings.SupportedGlobalShortcuts;
        ComboBox retentionPicker = this.FindControl<ComboBox>("RetentionPicker")!;
        retentionPicker.ItemsSource = RetentionChoices;
        retentionPicker.SelectedItem = RetentionChoices[1];
        retentionPicker.SelectionChanged += OnRetentionSelectionChanged;
        this.FindControl<Button>("ApplyRetentionButton")!.Click += OnApplyRetention;
        this.FindControl<Button>("ExportButton")!.Click += OnExportMetadata;
        this.FindControl<Button>("ImportButton")!.Click += OnImportMetadata;
        this.FindControl<Button>("ApplyShortcutButton")!.Click += OnApplyShortcut;
        this.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsCheckedChanged += OnLaunchAtLoginChanged;

        Opened += OnWindowOpened;
        PropertyChanged += OnWindowPropertyChanged;
        Closed += OnWindowClosed;
        Render("Ready for a drop.");
    }

    public ShelfSession Session { get; }
    public ShelfViewModel ViewModel { get; }

    public void ConfigureLocalDataForHost(ShelfDataService service, AppSettings settings)
    {
        dataService = service ?? throw new ArgumentNullException(nameof(service));
        currentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        ConfigurePrivacyControls(settings);
    }

    private void ConfigurePrivacyControls(AppSettings settings)
    {
        RetentionChoice? selected = RetentionChoices.FirstOrDefault(choice =>
            choice.ExpireOnExit == settings.ExpireOnExit && (choice.ExpireOnExit || choice.Retention == settings.Retention));
        if (selected is null)
        {
            selected = new($"Keep for {settings.Retention.TotalMinutes:0} minutes", settings.Retention, false);
            this.FindControl<ComboBox>("RetentionPicker")!.ItemsSource = RetentionChoices.Append(selected).ToArray();
        }
        this.FindControl<ComboBox>("RetentionPicker")!.SelectedItem = selected;
    }

    public void ConfigureRecoveryForHost(IResettableShelfStore store, BoundedMetadataCache cache)
    {
        resettableStore = store ?? throw new ArgumentNullException(nameof(store));
        metadataCache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public void ApplySnapshotForHost(StoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Session.ReplaceAll(snapshot.Items);
        ViewModel.ItemsReplaced();
        currentSettings = snapshot.Settings;
        ApplySettings(snapshot.Settings);
        Render(ViewModel.Announcement);
    }

    public void SetLocalDataLoadingForHost(bool loading) => localDataLoading = loading;

    public int PreviewRetentionForHost(TimeSpan retention, bool expireOnExit)
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        int affected = service.PreviewPolicyChange(Session, retention, expireOnExit);
        this.FindControl<TextBlock>("RetentionPreview")!.Text =
            $"Changing retention currently affects {affected} item{(affected == 1 ? string.Empty : "s")}.";
        return affected;
    }

    public async Task<PolicyChangeResult> ChangeRetentionForHostAsync(TimeSpan retention, bool expireOnExit)
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        await pendingPersistence;
        PolicyChangeResult result = await service.ChangePolicyAsync(Session, currentSettings, retention, expireOnExit);
        currentSettings = result.Settings;
        nativeSettings = result.Settings;
        ViewModel.ItemsReplaced();
        Render($"Retention saved. {result.AffectedItems} item{(result.AffectedItems == 1 ? string.Empty : "s")} affected.");
        return result;
    }

    public async Task<int> ClearUnpinnedForHostAsync()
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        await pendingPersistence;
        int removed = await service.ClearUnpinnedAsync(Session, currentSettings);
        ViewModel.ItemsReplaced();
        Render($"Cleared {removed} unpinned item{(removed == 1 ? string.Empty : "s")}.");
        return removed;
    }

    public async Task ClearAllForHostAsync()
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        await pendingPersistence;
        bool disableLogin = currentSettings.StartAtLogin;
        currentSettings = await service.ClearAllAsync(Session);
        nativeSettings = currentSettings;
        bool cacheCleared = true;
        try
        {
            metadataCache?.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cacheCleared = false;
        }
        if (nativeSettingsShell is not null)
        {
            _ = nativeSettingsShell.ConfigureShortcut(AppSettings.DefaultGlobalShortcut);
            if (disableLogin)
            {
                _ = nativeSettingsShell.SetLaunchAtLogin(false);
            }
        }
        ConfigurePrivacyControls(currentSettings);
        updatingNativeControls = true;
        this.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = currentSettings.GlobalShortcut;
        this.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsChecked = currentSettings.StartAtLogin;
        updatingNativeControls = false;
        ViewModel.ItemsReplaced();
        Render(cacheCleared
            ? "Cleared all local shelf metadata and restored default settings."
            : "Shelf metadata and settings were cleared, but the app cache could not be fully cleared. Retry clear all metadata.");
    }

    public byte[] ExportMetadataForHost()
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        return service.Export(Session, currentSettings);
    }

    public async Task<bool> ImportMetadataForHostAsync(ReadOnlyMemory<byte> json)
    {
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        try
        {
            await pendingPersistence;
            currentSettings = await service.ImportAsync(json, Session);
            nativeSettings = currentSettings;
            ConfigurePrivacyControls(currentSettings);
            ViewModel.ItemsReplaced();
            Render($"Imported {Session.Items.Count} item{(Session.Items.Count == 1 ? string.Empty : "s")}.");
            return true;
        }
        catch
        {
            Render("Import failed. Current shelf data was kept.");
            return false;
        }
    }

    public Task FlushPersistenceForHostAsync() => pendingPersistence;

    public async Task<int> PrepareForExitForHostAsync()
    {
        await pendingPersistence.ConfigureAwait(false);
        ShelfDataService service = dataService ?? throw new InvalidOperationException("Local data is not configured.");
        return await Task.Run(() => service.PrepareForExitAsync(Session, currentSettings)).ConfigureAwait(false);
    }

    private void QueuePersistenceForHost()
    {
        if (dataService is null)
        {
            return;
        }
        StoreSnapshot snapshot = new([.. Session.Items], currentSettings);
        pendingPersistence = PersistAfterAsync(pendingPersistence, snapshot);
    }

    private async Task PersistAfterAsync(Task previous, StoreSnapshot snapshot)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await Task.Run(() => dataService!.SaveSnapshotAsync(snapshot)).ConfigureAwait(false);
        }
        catch
        {
            Dispatcher.UIThread.Post(() => Render("Local metadata could not be saved. Your source files were not changed."));
        }
    }

    public void ConfigureNativeSettingsForHost(ISettingsStore settingsStore, INativeShell shell, AppSettings settings)
    {
        nativeSettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        nativeSettingsShell = shell ?? throw new ArgumentNullException(nameof(shell));
        nativeSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        currentSettings = settings;
        updatingNativeControls = true;
        this.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = settings.GlobalShortcut;
        this.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsChecked = settings.StartAtLogin;

        updatingNativeControls = false;
    }

    private void OnRetentionSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (dataService is not null && this.FindControl<ComboBox>("RetentionPicker")!.SelectedItem is RetentionChoice choice)
        {
            _ = PreviewRetentionForHost(choice.Retention, choice.ExpireOnExit);
        }
    }

    private async void OnApplyRetention(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (this.FindControl<ComboBox>("RetentionPicker")!.SelectedItem is RetentionChoice choice)
        {
            await RunDataActionAsync(async () => _ = await ChangeRetentionForHostAsync(choice.Retention, choice.ExpireOnExit));
        }
    }

    private async void OnExportMetadata(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (localDataLoading)
        {
            SetStatusText("Wait for local shelf metadata to finish loading.");
            return;
        }
        try
        {
            IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Drop Shelf metadata",
                SuggestedFileName = "drop-shelf-metadata.json",
                FileTypeChoices = [JsonFileType],
            });
            if (target is null)
            {
                return;
            }
            byte[] data = ExportMetadataForHost();
            await using Stream stream = await target.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(data);
            SetStatusText($"Exported {Session.Items.Count} item{(Session.Items.Count == 1 ? string.Empty : "s")} as metadata only.");
        }
        catch
        {
            SetStatusText("Metadata export failed. Current shelf data was kept.");
        }
    }

    private async void OnImportMetadata(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (localDataLoading)
        {
            SetStatusText("Wait for local shelf metadata to finish loading.");
            return;
        }
        try
        {
            IReadOnlyList<IStorageFile> selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Drop Shelf metadata",
                AllowMultiple = false,
                FileTypeFilter = [JsonFileType],
            });
            IStorageFile? source = selected.SingleOrDefault();
            if (source is null)
            {
                return;
            }
            await using Stream stream = await source.OpenReadAsync();
            byte[] data = await ReadBoundedImportAsync(stream);
            _ = await ImportMetadataForHostAsync(data);
        }
        catch
        {
            SetStatusText("Import failed. Current shelf data was kept.");
        }
    }

    private static async Task<byte[]> ReadBoundedImportAsync(Stream source)
    {
        using MemoryStream destination = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer);
            if (read == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + read > DomainLimits.MaxExportBytes)
            {
                throw new InvalidDataException("Import exceeds the metadata size limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static readonly FilePickerFileType JsonFileType = new("JSON metadata") { Patterns = ["*.json"] };

    private async void OnApplyShortcut(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (nativeSettingsUpdateInProgress || nativeSettingsShell is null || nativeSettingsStore is null ||
            this.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem is not string shortcut)
        {
            return;
        }

        nativeSettingsUpdateInProgress = true;
        SetNativeSettingsControlsEnabled(false);
        try
        {
            await pendingPersistence;
            NativeShellStatus status = nativeSettingsShell.ConfigureShortcut(shortcut);
            if (status != NativeShellStatus.Success)
            {
                this.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = nativeSettings.GlobalShortcut;
                ShowNativeShellStatus(status);
                return;
            }

            AppSettings changed = CopyNativeSettings(globalShortcut: shortcut);
            try
            {
                await nativeSettingsStore.SaveSettingsAsync(changed);
                nativeSettings = changed;
                currentSettings = changed;
                ShowNativeShellStatus(status);
            }
            catch
            {
                NativeShellStatus rollbackStatus = nativeSettingsShell.ConfigureShortcut(nativeSettings.GlobalShortcut);
                this.FindControl<ComboBox>("ShortcutPicker")!.SelectedItem = nativeSettings.GlobalShortcut;
                SetStatusText(rollbackStatus == NativeShellStatus.Success
                    ? "Could not save the shortcut. Your previous setting was kept."
                    : "Could not save the shortcut, and the previous native shortcut could not be restored. Review the shortcut setting before relying on it.");
            }
        }
        finally
        {
            nativeSettingsUpdateInProgress = false;
            SetNativeSettingsControlsEnabled(true);
        }
    }

    private async void OnLaunchAtLoginChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (updatingNativeControls || nativeSettingsShell is null || nativeSettingsStore is null)
        {
            return;
        }

        CheckBox toggle = this.FindControl<CheckBox>("LaunchAtLoginToggle")!;
        if (nativeSettingsUpdateInProgress)
        {
            updatingNativeControls = true;
            toggle.IsChecked = nativeSettings.StartAtLogin;
            updatingNativeControls = false;
            return;
        }

        bool requested = toggle.IsChecked == true;
        nativeSettingsUpdateInProgress = true;
        SetNativeSettingsControlsEnabled(false);
        try
        {
            await pendingPersistence;
            NativeShellStatus status = nativeSettingsShell.SetLaunchAtLogin(requested);
            if (status != NativeShellStatus.Success)
            {
                updatingNativeControls = true;
                toggle.IsChecked = nativeSettings.StartAtLogin;
                updatingNativeControls = false;
                SetStatusText("Could not change launch at login. Your previous setting was kept.");
                return;
            }

            AppSettings changed = CopyNativeSettings(startAtLogin: requested);
            try
            {
                await nativeSettingsStore.SaveSettingsAsync(changed);
                nativeSettings = changed;
                currentSettings = changed;
                SetStatusText("Launch-at-login setting saved.");
            }
            catch
            {
                NativeShellStatus rollbackStatus = nativeSettingsShell.SetLaunchAtLogin(nativeSettings.StartAtLogin);
                updatingNativeControls = true;
                toggle.IsChecked = nativeSettings.StartAtLogin;
                updatingNativeControls = false;
                SetStatusText(rollbackStatus == NativeShellStatus.Success
                    ? "Could not change launch at login. Your previous setting was kept."
                    : "Could not save launch at login, and the previous native setting could not be restored. Review the login setting before relying on it.");
            }
        }
        finally
        {
            nativeSettingsUpdateInProgress = false;
            SetNativeSettingsControlsEnabled(true);
        }
    }

    private void SetNativeSettingsControlsEnabled(bool enabled)
    {
        this.FindControl<ComboBox>("ShortcutPicker")!.IsEnabled = enabled;
        this.FindControl<Button>("ApplyShortcutButton")!.IsEnabled = enabled;
        this.FindControl<CheckBox>("LaunchAtLoginToggle")!.IsEnabled = enabled;
    }


    private AppSettings CopyNativeSettings(bool? startAtLogin = null, string? globalShortcut = null) =>
        AppSettings.Create(nativeSettings.DockEdge, nativeSettings.Retention, startAtLogin ?? nativeSettings.StartAtLogin,
            nativeSettings.ReduceMotion, nativeSettings.HighContrast, globalShortcut ?? nativeSettings.GlobalShortcut,
            nativeSettings.ExpireOnExit);

    public void ToggleVisibilityForHost()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
    }

    public void ShowNativeShellStatus(NativeShellStatus status)
    {
        string message = status switch
        {
            NativeShellStatus.Success => "Global shortcut ready.",
            NativeShellStatus.Conflict => "That shortcut is already in use. Use the tray or menu to open the shelf.",
            NativeShellStatus.PermissionDenied => "Global shortcut permission was denied. The shelf and tray or menu remain available.",
            NativeShellStatus.Unavailable => "Global shortcut is unavailable. Use the tray or menu to open the shelf.",
            NativeShellStatus.InvalidTarget => "That shortcut is not valid. Use the tray or menu to open the shelf.",
            NativeShellStatus.TargetMissing or NativeShellStatus.Failed => "Global shortcut could not be enabled. Use the tray or menu to open the shelf.",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        SetStatusText(message);
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        dockEdge = settings.DockEdge;
        RecoverDockToCurrentScreen();
    }

    public DropAdmissionResult AcceptDropForHost(InboundDropPayload payload, DateTimeOffset createdAt)
    {
        if (localDataLoading)
        {
            const string message = "Wait for local shelf metadata to finish loading before adding items.";
            Render(message);
            return new(false, [], message);
        }
        DropAdmissionResult result = admission.Admit(payload, createdAt);
        if (result.Accepted)
        {
            ViewModel.ExistingItemsAdded(result.Items);
            QueuePersistenceForHost();
        }
        Render(result.UserMessage);
        RestoreViewModelFocus();
        return result;
    }

    /// <summary>Builds the live Avalonia transfer used by the native drag backend.</summary>
    public Task<OutboundDataTransfer> BuildDataTransferForHostAsync(IReadOnlyList<ShelfItem> orderedItems) =>
        BuildDataTransferForHostAsync(orderedItems, NativeUrlFormat);

    /// <summary>Host-test seam for a platform URL format while retaining universal file and text formats.</summary>
    public async Task<OutboundDataTransfer> BuildDataTransferForHostAsync(
        IReadOnlyList<ShelfItem> orderedItems, DataFormat<string>? nativeUrlFormat)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        if (orderedItems.Count == 0 || (orderedItems.Count > 1 && orderedItems.Any(item => item.Payload is not FileReferencePayload)))
        {
            throw new ArgumentException("Select files, or one text or URL item.", nameof(orderedItems));
        }

        DataTransfer transfer = new();
        if (orderedItems.All(item => item.Payload is FileReferencePayload))
        {
            List<IStorageItem> resolved = new(orderedItems.Count);
            try
            {
                foreach (FileReferencePayload file in orderedItems.Select(item => (FileReferencePayload)item.Payload))
                {
                    IStorageItem? storageItem = file.ReferenceKind == FileReferenceKind.Directory
                        ? await StorageProvider.TryGetFolderFromPathAsync(file.Path)
                        : await StorageProvider.TryGetFileFromPathAsync(file.Path);
                    if (storageItem is null)
                    {
                        throw new InvalidOperationException("One or more selected files are unavailable.");
                    }
                    resolved.Add(storageItem);
                }

                foreach (IStorageItem item in resolved)
                {
                    transfer.Add(DataTransferItem.CreateFile(item));
                }
                return new(transfer, resolved);
            }
            catch
            {
                OutboundDataTransfer.DisposeStorageItems(resolved);
                throw;
            }
        }

        ShelfPayload payload = orderedItems[0].Payload;
        if (payload is TextPayload text)
        {
            transfer.Add(DataTransferItem.CreateText(text.Text));
            return new(transfer);
        }

        string url = ((UrlPayload)payload).Url.AbsoluteUri;
        DataTransferItem urlItem = DataTransferItem.CreateText(url);
        if (nativeUrlFormat is not null)
        {
            urlItem.Set(nativeUrlFormat, url);
        }
        transfer.Add(urlItem);
        return new(transfer);
    }

    internal static DataFormat<string>? NativeUrlFormat => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? DataFormat.CreateStringPlatformFormat(WindowsDragDropFormats.Url)
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? DataFormat.CreateStringPlatformFormat(MacPasteboardFormats.Url)
        : null;

    /// <summary>Converts Avalonia's native-backed transfer without discarding malformed file members.</summary>
    public static InboundDropPayload ReadDataTransferForHost(
        IDataTransfer transfer, DataFormat<string>? nativeUrlFormat)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return ReadDataTransferForHost(transfer, nativeUrlFormat, GetStoragePathOrInvalidMarker);
    }

    public static InboundDropPayload ReadDataTransferForHost(
        IDataTransfer transfer,
        DataFormat<string>? nativeUrlFormat,
        Func<IStorageItem, StorageDropReference> mapStorageItem)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(mapStorageItem);
        StorageDropReference[]? files = transfer.TryGetFiles()?.Select(item =>
        {
            try
            {
                return item is null ? InvalidStorageReference : mapStorageItem(item);
            }
            catch
            {
                return InvalidStorageReference;
            }
        }).ToArray();
        string? url = nativeUrlFormat is null ? null : transfer.TryGetValue(nativeUrlFormat);
        return new(files?.Select(file => file.Path).ToArray(), url, transfer.TryGetText(),
            files?.Select(file => file.Kind).ToArray());
    }

    private static readonly StorageDropReference InvalidStorageReference = new("\0", FileReferenceKind.File);

    private static StorageDropReference GetStoragePathOrInvalidMarker(IStorageItem item)
    {
        try
        {
            Uri path = item.Path;
            FileReferenceKind kind = item switch
            {
                IStorageFolder => FileReferenceKind.Directory,
                IStorageFile => FileReferenceKind.File,
                _ => throw new InvalidOperationException("Unsupported storage item type."),
            };
            return path.IsAbsoluteUri && path.IsFile
                ? new(path.LocalPath, kind)
                : InvalidStorageReference;
        }
        catch
        {
            return InvalidStorageReference;
        }
    }

    public DropAdmissionResult AcceptDataTransferForHost(
        IDataTransfer transfer,
        DataFormat<string>? nativeUrlFormat,
        DateTimeOffset createdAt,
        Func<IStorageItem, StorageDropReference>? mapStorageItem = null)
    {
        try
        {
            InboundDropPayload payload = ReadDataTransferForHost(
                transfer, nativeUrlFormat, mapStorageItem ?? GetStoragePathOrInvalidMarker);
            if (payload.FilePaths?.Any(path => path == InvalidStorageReference.Path) == true)
            {
                const string unsupported = "That drop does not contain supported content.";
                Render(unsupported);
                return DropAdmissionResult.Rejected(unsupported);
            }
            return AcceptDropForHost(payload, createdAt);
        }
        catch
        {
            const string message = "That drop does not contain supported content.";
            Render(message);
            return DropAdmissionResult.Rejected(message);
        }
    }

    private void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        try
        {
            DropAdmissionResult result = AcceptDataTransferForHost(eventArgs.DataTransfer, NativeUrlFormat, DateTimeOffset.UtcNow);
            eventArgs.DragEffects = result.Accepted ? DragDropEffects.Copy : DragDropEffects.None;
        }
        catch
        {
            Render("That drop does not contain supported content.");
            eventArgs.DragEffects = DragDropEffects.None;
        }
        finally
        {
            eventArgs.Handled = true;
        }
    }

    private void Render(string status)
    {
        StackPanel panel = this.FindControl<StackPanel>("ShelfItems")!;
        panel.Children.Clear();
        IReadOnlyList<ShelfItem> fileItems = Session.Items
            .Where(item => item.Payload is FileReferencePayload)
            .ToArray();
        if (fileItems.Count >= 2)
        {
            TextBlock batchHandle = CreateDragHandle(
                "FileBatchDragHandle", $"Drag all {fileItems.Count} files", fileItems);
            panel.Children.Add(batchHandle);
        }

        for (int index = 0; index < ViewModel.Cards.Count; index++)
        {
            ShelfCardViewModel cardModel = ViewModel.Cards[index];
            ShelfItem item = Session.Items[index];
            TextBlock content = new()
            {
                Text = BuildVisibleCardText(cardModel),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            ToggleButton card = new()
            {
                Name = $"ItemName{index}",
                Content = content,
                Tag = new ShelfCardTag(item),
                IsChecked = cardModel.IsSelected,
                MinHeight = ShelfGeometry.MinimumReachableSize,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            card.Classes.Add("shelf-card");
            AutomationProperties.SetName(card, cardModel.AccessibleName);
            AutomationProperties.SetControlTypeOverride(card, AutomationControlType.ListItem);
            AutomationProperties.SetPositionInSet(card, index + 1);
            AutomationProperties.SetSizeOfSet(card, ViewModel.Cards.Count);
            AutomationProperties.SetItemStatus(card, cardModel.IsSelected ? "Selected" : "Not selected");
            card.Click += (_, _) =>
            {
                ViewModel.ToggleSelection(cardModel.Id);
                Render(ViewModel.Announcement);
                RestoreViewModelFocus();
            };
            card.PointerPressed += OnItemPointerPressed;
            card.PointerMoved += OnItemPointerMoved;
            card.PointerReleased += OnItemPointerReleased;
            panel.Children.Add(card);
        }
        this.FindControl<TextBlock>("EmptyShelfMessage")!.IsVisible = Session.Items.Count == 0;
        SetStatusText(status);
    }

    private void SetStatusText(string status)
    {
        this.FindControl<TextBlock>("DropStatus")!.Text = status;
        this.FindControl<TextBlock>("LiveStatus")!.Text = status;
    }

    private static string BuildVisibleCardText(ShelfCardViewModel card)
    {
        string source = card.SourceHint is null ? string.Empty : $" · From {card.SourceHint}";
        string states = $" · {(card.IsPinned ? "Pinned" : "Not pinned")}{(card.IsMissing ? " · Unavailable" : string.Empty)}";
        return $"{card.TypeLabel} · {card.DisplayLabel}{source} · {card.AgeLabel}{states}";
    }

    private TextBlock CreateDragHandle(string name, string text, IReadOnlyList<ShelfItem> items)
    {
        TextBlock handle = new()
        {
            Name = name,
            Text = text,
            Tag = items,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        handle.PointerPressed += OnItemPointerPressed;
        handle.PointerMoved += OnItemPointerMoved;
        handle.PointerReleased += OnItemPointerReleased;
        return handle;
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control control && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            dragStart = eventArgs.GetPosition(control);
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        IReadOnlyList<ShelfItem>? items = sender switch
        {
            Control { Tag: IReadOnlyList<ShelfItem> batch } => batch,
            Control { Tag: ShelfCardTag card } => [card.Item],
            _ => null,
        };
        if (sender is not Control control || items is null || dragStart is not { } start || dragInProgress ||
            !eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point current = eventArgs.GetPosition(control);
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4)
        {
            return;
        }

        dragStart = null;
        dragInProgress = true;
        try
        {
            await RunOutboundDragForHostAsync(items, async transfer =>
            {
                _ = await DragDrop.DoDragDropAsync(eventArgs, transfer, DragDropEffects.Copy);
            });
        }
        finally
        {
            dragInProgress = false;
        }
    }

    public async Task RunOutboundDragForHostAsync(
        IReadOnlyList<ShelfItem> orderedItems, Func<IDataTransfer, Task> performDragAsync)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        ArgumentNullException.ThrowIfNull(performDragAsync);
        try
        {
            using OutboundDataTransfer transfer = await BuildDataTransferForHostAsync(orderedItems);
            await performDragAsync(transfer.Data);
        }
        catch
        {
            Render("The drag could not be completed.");
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs eventArgs) => dragStart = null;

    public void ShowShelfState(ShelfUiState state)
    {
        TextBlock message = this.FindControl<TextBlock>("StateMessage")!;
        Button retry = this.FindControl<Button>("RetryButton")!;
        Button reset = this.FindControl<Button>("ResetLocalDataButton")!;
        string status;
        (message.Text, retry.IsVisible, status) = state switch
        {
            ShelfUiState.Ready => (string.Empty, false, "Shelf ready."),
            ShelfUiState.Loading => ("Loading shelf items…", false, "Loading shelf items…"),
            ShelfUiState.Expired => ("Expired items were removed according to your retention setting.", false,
                "Expired items were removed according to your retention setting."),
            ShelfUiState.Unavailable => ("Some source items are unavailable. Remove them or try again after reconnecting the source.", false,
                "Some source items are unavailable. Remove them or try again after reconnecting the source."),
            ShelfUiState.RecoverableError => ("Shelf items could not be loaded.", true, "Shelf items could not be loaded."),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        reset.IsVisible = state == ShelfUiState.RecoverableError;
        SetStatusText(status);
    }

    private async void OnResetLocalData(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (resettableStore is null || metadataCache is null || dataService is null)
        {
            SetStatusText("Local metadata reset is unavailable.");
            return;
        }
        try
        {
            await pendingPersistence;
            await resettableStore.ResetAsync();
            metadataCache.Clear();
            ShelfLoadResult loaded = await dataService.LoadAsync();
            ApplySnapshotForHost(loaded.Snapshot);
            SetLocalDataLoadingForHost(false);
            ShowShelfState(ShelfUiState.Ready);
            SetStatusText("Local shelf metadata was reset. Source files were not changed.");
        }
        catch
        {
            ShowShelfState(ShelfUiState.RecoverableError);
        }
    }

    private async void OnRetryShelfLoad(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (retryInProgress)
        {
            return;
        }
        retryInProgress = true;
        ShowShelfState(ShelfUiState.Loading);
        try
        {
            if (retryShelfLoad is null)
            {
                throw new InvalidOperationException("No shelf-load retry action is configured.");
            }
            await retryShelfLoad();
            if (string.Equals(this.FindControl<TextBlock>("StateMessage")!.Text,
                "Loading shelf items…", StringComparison.Ordinal))
            {
                ShowShelfState(ShelfUiState.Ready);
            }
            RestoreViewModelFocus();
        }
        catch
        {
            ShowShelfState(ShelfUiState.RecoverableError);
        }
        finally
        {
            retryInProgress = false;
        }
    }

    private async void OnShelfKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        bool commandButtonFocused = eventArgs.Source is Button and not ToggleButton;
        eventArgs.Handled = await HandleShelfShortcutForHostAsync(
            eventArgs.Key, eventArgs.KeyModifiers, commandButtonFocused);
    }

    public async Task<bool> HandleShelfShortcutForHostAsync(
        Key key, KeyModifiers modifiers, bool commandButtonFocused)
    {
        bool control = modifiers.HasFlag(KeyModifiers.Control);
        bool alt = modifiers.HasFlag(KeyModifiers.Alt);
        if (commandButtonFocused && key is Key.Enter or Key.Space)
        {
            return false;
        }
        if (control && key == Key.A)
        {
            RunAndRender(ViewModel.SelectAll);
        }
        else if (control && key == Key.C)
        {
            await RunViewModelActionAsync(ViewModel.CopySelectedAsync);
        }
        else if (key == Key.Delete)
        {
            RunMutatingAndRender(() => _ = ViewModel.RemoveSelected());
        }
        else if (alt && key is Key.Up or Key.Left)
        {
            RunMutatingAndRender(() => _ = ViewModel.MoveSelected(-1));
        }
        else if (alt && key is Key.Down or Key.Right)
        {
            RunMutatingAndRender(() => _ = ViewModel.MoveSelected(1));
        }
        else if (key == Key.Enter)
        {
            await RunViewModelActionAsync(ViewModel.OpenSelectedAsync);
        }
        else if (key == Key.P)
        {
            RunMutatingAndRender(ViewModel.TogglePinned);
        }
        else if (key == Key.Escape)
        {
            SetCollapsed(true);
        }
        else
        {
            return false;
        }
        return true;
    }

    private async Task RunViewModelActionAsync(Func<Task> action)
    {
        Task running = action();
        SetActionButtonsEnabled(!ViewModel.IsActionInProgress);
        try
        {
            await running;
        }
        finally
        {
            SetActionButtonsEnabled(!ViewModel.IsActionInProgress);
            Render(ViewModel.Announcement);
            RestoreViewModelFocus();
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        this.FindControl<Button>("CopyButton")!.IsEnabled = enabled;
        this.FindControl<Button>("OpenButton")!.IsEnabled = enabled;
        this.FindControl<Button>("RevealButton")!.IsEnabled = enabled;
    }

    private void RunMutatingAndRender(Action action)
    {
        if (localDataLoading)
        {
            Render("Wait for local shelf metadata to finish loading.");
            return;
        }
        RunAndRender(action);
        QueuePersistenceForHost();
    }

    private async Task RunDataActionAsync(Func<Task> action)
    {
        if (localDataLoading)
        {
            Render("Wait for local shelf metadata to finish loading.");
            return;
        }
        try
        {
            await action();
            RestoreViewModelFocus();
        }
        catch
        {
            Render("Local metadata could not be changed. Your source files were not changed.");
        }
    }

    private void RunAndRender(Action action)
    {
        action();
        Render(ViewModel.Announcement);
        RestoreViewModelFocus();
    }

    private void RestoreViewModelFocus()
    {
        if (ViewModel.IsCollapsed)
        {
            _ = this.FindControl<Button>("ExpandButton")!.Focus();
            return;
        }

        Control target = ViewModel.FocusedItemId is Guid id
            ? this.FindControl<StackPanel>("ShelfItems")!.Children
                .OfType<Control>().First(control => control is ToggleButton { Tag: ShelfCardTag tag } && tag.Item.Id == id)
            : this.FindControl<TextBlock>("EmptyShelfMessage")!;
        _ = target.Focus();
    }

    private void SetCollapsed(bool collapsed)
    {
        ShelfBounds? restoreBounds = collapsed ? null : expandedBounds;
        if (collapsed && !ViewModel.IsCollapsed)
        {
            expandedBounds = CurrentBounds();
        }
        ViewModel.SetCollapsed(collapsed);
        this.FindControl<Grid>("ExpandedShelf")!.IsVisible = !collapsed;
        Button expand = this.FindControl<Button>("ExpandButton")!;
        expand.IsVisible = collapsed;
        if (collapsed)
        {
            _ = expand.Focus();
        }
        else
        {
            Render(ViewModel.Announcement);
            RestoreViewModelFocus();
        }
        SetStatusText(ViewModel.Announcement);
        RecoverDockToCurrentScreen(restoreBounds);
    }

    private void OnWindowOpened(object? sender, EventArgs eventArgs)
    {
        activeScreens = Screens;
        activeScreens.Changed += OnScreensChanged;
        RecoverDockToCurrentScreen();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (activeScreens is not null)
        {
            activeScreens.Changed -= OnScreensChanged;
            activeScreens = null;
        }
    }

    private void OnScreensChanged(object? sender, EventArgs eventArgs) => RecoverDockToCurrentScreen();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (string.Equals(eventArgs.Property.Name, nameof(RenderScaling), StringComparison.Ordinal))
        {
            RecoverDockToCurrentScreen();
        }
    }

    public static double ResolveTargetScaleForHost(double selectedScreenScaling, double windowScaling) =>
        selectedScreenScaling > 0 ? selectedScreenScaling : windowScaling > 0 ? windowScaling : 1;

    public ShelfBounds RecoverDockForHost(ShelfBounds current, ShelfBounds workArea, double renderScaling = 1) =>
        RecoverDock(current, workArea, previousOverride: null, renderScaling);

    private void RecoverDockToCurrentScreen(ShelfBounds? previousOverride = null)
    {
        if (recoveringDock)
        {
            return;
        }
        recoveringDock = true;
        try
        {
            Screens? screens = activeScreens;
            if (screens is null)
            {
                return;
            }
            Screen? screen = screens.ScreenFromWindow(this) ?? screens.Primary;
            if (screen is null)
            {
                return;
            }
            PixelRect work = screen.WorkingArea;
            double scale = ResolveTargetScaleForHost(screen.Scaling, RenderScaling);
            ShelfBounds current = CurrentBounds(scale);
            ShelfBounds recovered = RecoverDock(
                current, new ShelfBounds(work.X, work.Y, work.Width, work.Height), previousOverride, scale);
            Position = new PixelPoint(recovered.Left, recovered.Top);
            Width = recovered.Width / scale;
            Height = recovered.Height / scale;
        }
        finally
        {
            recoveringDock = false;
        }
    }

    private ShelfBounds RecoverDock(
        ShelfBounds current, ShelfBounds workArea, ShelfBounds? previousOverride, double renderScaling)
    {
        ShelfBounds recovered = ShelfGeometry.Recover(
            previousOverride ?? current, workArea, dockEdge, ViewModel.IsCollapsed, renderScaling);
        if (!ViewModel.IsCollapsed)
        {
            expandedBounds = recovered;
        }
        return recovered;
    }

    private ShelfBounds CurrentBounds(double? scaleOverride = null)
    {
        double scale = scaleOverride ?? (RenderScaling <= 0 ? 1 : RenderScaling);
        PixelSize size = PixelSize.FromSize(ClientSize, scale);
        return new ShelfBounds(Position.X, Position.Y, size.Width, size.Height);
    }

    private sealed record RetentionChoice(string Label, TimeSpan Retention, bool ExpireOnExit)
    {
        public override string ToString() => Label;
    }

    private sealed record ShelfCardTag(ShelfItem Item);
}
